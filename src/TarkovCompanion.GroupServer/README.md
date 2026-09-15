# Group server

A small relay so a group of friends can see each other on one map during a raid.

Live member state is memory-only. Each member publishes their own state, the server keeps the
last thing each of them said in memory, and hands back everyone else's. A member who stops
publishing disappears after three minutes, and restarting the server forgets everyone. The state
directory separately persists waypoints, registered rooms, problem reports, and updater status;
it does not record member-position history.

## What is shared

Only what the sender publishes about themselves: their display name, which map they are on,
their raid state and side, the position and heading from their own screenshot with its age,
their loadout and the quests they are working on.

This is a deliberate departure from the desktop application's usual promise that nothing from
the game's logs leaves the machine. It happens only when somebody turns it on, and what is
sent is listed in `GroupContracts.cs` in full so the promise can be read rather than trusted.

## Access

One value: the group key. Whoever types the same key is in the same group.

In open mode the server has no registered room verifier to compare a key against. It receives the
reusable bearer key on every request, then hashes it and buckets members by the result, so a key
nobody else uses names a room nobody else is in rather than being refused. Stock source does not
intentionally persist or log the raw key, but the receiving relay and any unprotected transport
can read it. Scoped credentials and transport hardening are tracked in #304 and #310.

This replaced a room name plus a secret set in the server's environment. That arrangement was
worse in every way: a member could not choose the secret, the person running the container had
to hand it out, and anyone who typed a different one was refused with a 401 and saw an empty
list with no reason given. Two values meant two ways to be wrong and the failure looked the
same either way.

What is genuinely different: a stranger who reaches this server can invent a key and have a
room of their own, exactly as they could have invented a room name before. What they cannot do
is join a group whose key they do not know, because the room is the hash of that key and is not
discoverable from outside. The part that was protecting the group still is; the part that was
ceremony is gone.

Keys shorter than eight characters are refused. That is not access control, since there is
nothing to check against. It stops somebody believing that `a` keeps strangers out.

## Running it

```
dotnet run --project src/TarkovCompanion.GroupServer
```

No configuration. Behind a reverse proxy that terminates TLS; it listens on plain HTTP and
should never be exposed directly.

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/health` | Liveness, for the proxy and for a person checking it is up |
| `POST` | `/state` | Publish yourself, receive everyone else and the group's marks |
| `GET` | `/state` | Read the room without joining it, for the second screen |
| `DELETE` | `/state/{name}` | Leave immediately rather than timing out |
| `POST` | `/waypoints` | Mark a place for the group; it stays until cleared |
| `POST` | `/waypoints/{id}/reached` | Record that somebody got there |
| `DELETE` | `/waypoints/{id}` | Remove one |
| `DELETE` | `/waypoints?mapId=&reachedOnly=` | Clear a map's, or only the reached ones |
| `POST` | `/pings` | Point at a place; it fades after 45 seconds |
| `GET` | `/catalog` | What game data the server is holding, and its tags |
| `GET` | `/catalog/{mode}/{endpoint}` | One catalog payload, with a strong tag |
| `GET` | `/` and `/tablet` | The second screen |

Everything about a group carries the key in an `X-Group-Key` header, and the room is not in the
URL because the key decides it. The catalog and the page itself do not: the catalog is public
data anybody can fetch from json.tarkov.dev without asking, and the page has nothing in it until
somebody types a key into it.

One exchange does both halves, so there is no connection to hold open and no subscription to
leak. A companion that is not running sends nothing and therefore shows nothing.

## Marks

A **waypoint** is a plan and stays until somebody clears it. A **ping** says "look here" and
fades after forty-five seconds. Having both is the point: a plan that quietly became forty stale
"look here" marks would be worse than either alone.

They ride back on the `/state` exchange a client already makes every few seconds, so nothing
polls a second endpoint to notice the group moved a waypoint.

Both belong to the group rather than to whoever dropped them, so anyone in the group may clear
them. A server that tracked who owned what would need identities, and this one deliberately has
none.

Bounded per room: sixty waypoints and thirty pings. Past that the oldest goes, so somebody
leaning on a mouse button loses their stalest plan rather than being refused or filling the
server.

## The game-data catalog

Every client otherwise syncs several megabytes from json.tarkov.dev into its own database, on
its own schedule, over its own connection. A group of five does that five times for five
identical answers. This server is already running and already reachable by all of them.

```
GET /catalog                      what is held: path, tag, when it was fetched, how big
GET /catalog/{mode}/{endpoint}    the payload, with a strong ETag
```

`mode` is `regular` or `pve`; `endpoint` is one of a fixed list (`items`, `maps`, `tasks`,
`hideout`, `traders`, `barters`, `crafts`, `ammo`, `achievements`, `status`). **An allowlist,
not a pattern** — an open proxy on a public address is somebody else's bandwidth bill, and "it
only forwards to one host" stops being true the first time the path is built from user input.

The tag is the SHA-256 of the bytes, which makes a snapshot content-addressed: two clients
holding the same tag hold the same catalog, and a client that already has it gets a 304 and no
body at all. Held for an hour before upstream is asked again.

**Outside the group key, deliberately.** The catalog is public data anybody can fetch from
json.tarkov.dev without asking permission, so a key here would protect nothing and would stop a
client that has not been configured for a group from using the mirror at all.

**It is never a dependency.** A client tries the mirror once, and on anything other than a good
answer goes straight to upstream — which is what it did before this existed. A server with
nothing held and no route upstream answers 503 rather than an error body, so the client falls
back instead of caching a failure under a strong tag. The moment the mirror becomes required it
has stopped being an optimisation, and that is not a trade this application makes.

### What this is not, yet

It serves the upstream payload byte for byte. The larger prize — fixing the parser once here
when tarkov.dev next changes shape, rather than shipping a client build — needs the server to
publish its own stable schema rather than upstream's, which means the deserialisation models
moving somewhere both can see. That is a separate piece of work; this endpoint is where it
would be served from.

## The second screen

Alt-tabbing out of a raid to drop a waypoint is the thing that makes a companion not worth
using, and a tablet cannot run the desktop application at all — so the choice there is a web
surface or nothing. `GET /` serves one.

```
GET /          the page
GET /tablet    the same page
GET /state     the room, without joining it
```

One embedded HTML file. No framework, no build step, no CDN, no font host: it reaches nothing
outside the server that served it, so a tablet on a house network with no internet still works.

`GET /state` exists for it. The companion's exchange is a POST because it has a position to
contribute; a second screen has none — it is not in the raid — and joining as a member would
put a phantom marker in the group and a phantom name in everybody's panel. So the GET returns
everyone, including whoever is reading, because the reader is not one of them.

**It is a schematic, not the map,** and it says so on the page. It has no artwork and no
projection: it plots everybody's world coordinates relative to each other on a grid. That is
enough to see who is where and enough to point at a spot and say "there", and pretending
otherwise would send somebody to the wrong place.

Tapping it drops a waypoint, or a ping with the mode switched — the same two things the desktop
client's right-click does, through the same two endpoints.

**It must never become a dependency.** The desktop client stays complete on its own and
somebody playing alone needs none of this.
