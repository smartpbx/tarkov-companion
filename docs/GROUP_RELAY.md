# Connecting a client to the group relay

Anything that can make an HTTPS request can join a group. This document is the whole protocol.

    https://tarkov.mannerow.net

## The group key

One value, agreed between the people in the group and typed into each of their clients. It is not
configured as a per-room server secret, but the receiving relay sees the reusable bearer key on
every request before hashing it to a room identifier.

It hashes the key you send and buckets members by the result, so the key is both *which group*
and *proof you are in it*. Type the same key as your friends and you see each other. Type a
different one and you are in a different group, quietly, rather than being refused.

Keys under eight characters are rejected. That is not access control, since an open relay has no
registered room to check a key against; it stops somebody believing `a` keeps strangers out.

The address above is public. Knowing where the relay is does not get you into a group, because
the room is that key's hash, but nothing limits how many keys somebody may try, so a short or
human-chosen key can be guessed. Use a long random key; attempt limits and scoped credentials
are open work in #304 and #310.

Send it as `X-Group-Key` on every request.

## Publish yourself, receive everyone else

One exchange does both halves, so there is nothing to subscribe to and no connection to hold
open. Call it every few seconds while you want to be visible.

    POST /state
    X-Group-Key: <the group's key>
    Content-Type: application/json

```json
{
  "name": "MaxGooner",
  "mapId": "customs",
  "raidState": "InRaid",
  "side": "pmc",
  "x": 56.16,
  "z": 110.52,
  "heading": 214.5,
  "positionAge": 12.4,
  "loadout": ["M4A1", "Slick"],
  "quests": ["Debut"],
  "questIds": ["5936d90786f7742b1420ba5b"]
}
```

Everything except `name` may be null or omitted. Optional fields have been added since this
example was written; `GroupMemberState` in `src/TarkovCompanion.GroupServer/GroupContracts.cs`
is the full list, and each one is an init property so a client that predates it is neither
required to send it nor refused for sending nothing. `name` is required, is at most 48 characters,
and is the identity within the group: publishing again under the same name replaces your
previous entry rather than adding a second one.

- `mapId` — whatever your client calls the map. Clients should agree; ours uses tarkov.dev's
  normalised names, such as `customs` and `streets-of-tarkov`.
- `raidState` — `Menu`, `LoadingRaid`, `InRaid` or `PostRaid`.
- `x` and `z` — world coordinates, exactly as the game writes them into a screenshot filename.
  No transform is applied; every client projects them with its own map transform.
- `heading` — degrees, 0..360, a compass bearing in game axes where +z is 0 and +x is 90.
- `positionAge` — seconds since that position was recorded, so others can judge how stale the
  marker is rather than guessing.
- `sinceSeconds` — **server-set, read-only.** Seconds since the relay last heard from that
  member at all. Do not send it: a member has no idea how long ago its own last message
  arrived. This is a different question from `positionAge`, and the difference is the point —
  a companion that crashed mid-raid keeps republishing nothing, so its position age freezes and
  the marker reads fresh for the full three minutes until the room forgets it. Our client draws
  a member as a guess after three missed exchanges, which is fifteen seconds.
- `quests` — what you are working on, for a squadmate to read. At most 24, and ours sends 5,
  which is what fits in a panel.
- `questIds` — the same quests by catalog id, for a squadmate's client to act on: with an id it
  can ask its own quest catalog which maps those quests point at and rank where the group's
  lists overlap. At most 40, each at most 64 characters. Ids are resolved against the
  receiver's own catalog, so one it does not carry is passed over rather than guessed at.

The reply is everyone else in the group:

```json
{
  "room": "9f2c…",
  "members": [ { "name": "…", "mapId": "…", "…": "…" } ],
  "serverUtc": "2026-09-13T00:31:00+00:00"
}
```

You are never in your own `members` list. `room` is the hash, returned so a client can notice it
has been talking to a different group than it thought.

## Marks

A **waypoint** is a plan and stays until somebody clears it. A **ping** says "look here" and
fades after forty-five seconds. Having both is the point: a plan that quietly became forty stale
"look here" marks would be worse than either alone.

Both belong to the group rather than to whoever dropped them, so anyone in the group may clear
any of them. A server that tracked who owned what would need identities, and this one
deliberately has none.

They come back on the `/state` reply, so a client that is already publishing every few seconds
learns about them without polling anything:

```json
{
  "room": "9f2c…",
  "members": [],
  "waypoints": [
    { "id": 4, "by": "MaxGooner", "mapId": "customs", "x": 56.1, "y": -2.9, "z": 110.5,
      "label": "Dorms", "createdUtc": "…", "completedUtc": null, "completedBy": null }
  ],
  "pings": [
    { "id": 5, "by": "Geo", "mapId": "customs", "x": 12.0, "y": 1.0, "z": 44.0,
      "label": null, "createdUtc": "…" }
  ],
  "serverUtc": "…"
}
```

### Dropping one

    POST /waypoints
    POST /pings
    X-Group-Key: <the group's key>

```json
{ "by": "MaxGooner", "mapId": "customs", "x": 56.16, "y": -2.95, "z": 110.52, "label": "Dorms" }
```

`by` and `mapId` are required; `label` is optional and is cut at 64 characters. `y` is the
height, which matters for a map with floors. The reply is the created mark, including the `id`
the server assigned, so two members marking at once cannot collide.

### Reaching, removing, clearing

    POST   /waypoints/{id}/reached      { "by": "MaxGooner" }
    DELETE /waypoints/{id}
    DELETE /waypoints?mapId=customs&reachedOnly=true

Reaching one records who got there and will not overwrite whoever arrived first. Clearing
returns how many went. Omit `mapId` to clear every map.

### Limits

Sixty waypoints and thirty pings per group. Past that the oldest goes, so somebody leaning on a
mouse button loses their stalest plan rather than being refused or filling the server.

## Leaving

    DELETE /state/{name}
    X-Group-Key: <the group's key>

Optional in the sense that nothing breaks without it: a member who simply stops publishing
disappears after three minutes. Those three minutes are the reason to call it — until then the
member is drawn on everybody's map, apparently still in the raid.

Our client calls it in three cases, and the third is the one that is easy to miss:

1. **Sharing is turned off.** Otherwise the player vanishes three minutes after they thought
   they had gone.
2. **The service is disposed**, which is the application closing.
3. **The display name changes.** A room is keyed by display name, so a rename is a new member
   as far as the relay is concerned, and the old one keeps its marker — the group sees the
   player twice, once where they are and once where they were.

Withdraw the name that was **published**, not the one currently configured. In the rename case
those differ, and a request built from current settings removes the marker just created and
leaves the stale one standing.

## Health

    GET /health   ->   {"status":"ok","protocol":1,"version":"1.0.548", ...}

No key required. `protocol` is the number described above; `version` and `commit` say which
build is answering.

## Who may have a room

By default, anybody who can reach the relay. A key names a room, so a stranger who reaches the
relay can invent a key and be in one — exactly as they could have invented a room name before
keys replaced room names. What they cannot do is join *your* room without knowing or guessing
its key, because its name is the hash of that key.

That is enough for a relay nobody else knows the address of, and stops being enough when one
does. So an operator can register the rooms that are meant to exist, from the panel at `/admin`:

- **With nothing registered the relay is open**, and behaves exactly as it always has.
- **Registering the first room closes it.** From then on, a key whose room is not registered is
  refused with a 403 and the client says so rather than showing an empty group.

Registering a room does not mean handing the relay a key. Three ways in, all of which end as the
same stored hash:

| Way | When |
| --- | --- |
| Have a key generated | A new group. The key is shown once, on the page, and only its hash is stored. |
| Give a key the group already uses | Rooms that predate the list, when you know the key. |
| Adopt a room the relay is holding | Rooms that predate the list when you do not. |

The third is the one that makes closing an established relay painless: the panel lists the rooms
that currently have members and are not registered, and adopting one keeps the friends already
in it. Anything still on that list after you have adopted your own is somebody you did not
invite.

## Landmarks for the second screen

`GET /landmarks` returns every map's recognisable places in world coordinates, keyed by the
map's normalised name. No key: this is public game data, like `/catalog`, and requiring one
would mean the tablet could draw nothing until somebody had typed a group key.

```json
{ "customs": [ {"k":"e","n":"Old Azs Gate","f":"scav","x":300.5,"z":-198.5},
               {"k":"t","n":"Transit to shoreline","x":650.6,"z":124.9},
               {"k":"l","x":577.7,"z":4.1} ] }
```

`k` is `e` extract, `t` transit, `l` lock, `p` place. A null name or faction is omitted rather
than written, which is most of the locks.

It is derived from the mirrored map catalog rather than being a second source, and it exists
because of a measurement: that catalog is 8,542,745 bytes, 780,279 gzipped, and what a schematic
can actually draw out of it is 19,509 bytes for all fifteen maps at once. The difference is
whether a phone on a sofa opens the page.

Only extracts carry a name anybody would recognise. A transit's own `description` is a
translation token, so it is named for where it leads; a lock is `lockType: "door"` thirty-six
times over on Customs, so it is drawn and not labelled. Spawns are left out — 278 nameless
points on one map is a texture, not a landmark.

**Places come from a second file.** Big Red, Dorms, Fortress, Power Station — the names people
actually say — are in the map artwork's label layer, which lives in the-hideout's `maps.json`
and not in the game-data catalog this relay mirrors. The desktop has read that file since the
map was drawn, and it is what `WaypointNaming` names a mark from, so without it a mark made on
the tablet could never read the same as one made at the desk. 109,867 bytes upstream, 303 labels
across ten maps. If that host is unreachable the landmarks lose their place names and nothing
else; the relay asks again an hour later rather than on every request.

The whole answer is 33,015 bytes for all fifteen maps.

## Searching the catalog

`GET /search?q=salewa` returns up to twelve items with their prices. No key, like `/catalog` and
`/landmarks`.

```json
[{"id":"544fb45d4bdc2dee738b4568","name":"Salewa first aid kit",
  "shortName":"Salewa","flea":31747,"base":15090}]
```

`flea` is the 24-hour average and is absent when nothing has traded; `base` is the game's own
number and is never printed as though it were a selling price.

Searched here rather than on the page for the same reason landmarks are derived here: the items
catalog is 16,713,367 bytes, 1,887,826 gzipped, for 5,320 items. One search is about 460 bytes.

Two files go into it. Every item's `name` in the catalog is a token — literally `"{id} Name"` —
and `items_en` is the dictionary that turns it into "Colt M4A1 5.56x45 assault rifle". All 5,320
resolve through it. The desktop has always fetched both; the mirror carried only the first until
now, so anything searching it would have matched ids.

Matching is word by word rather than as one string: "factory key" finds "Factory emergency exit
key", which a contiguous match never would. Every word has to appear somewhere, and where they
appear is what orders the results — an exact short name first, then a name starting with what was
typed, then every word starting a word of the name.

## What the server does not do

- It writes no position history. A member's position and last few trail points stay in memory
  until a few minutes after they go quiet; a reached waypoint does record who reached it and when.
- It has no accounts and no identities beyond the display name you send.
- It receives your key, hashes it to select the room, and does not intentionally log or persist
  the raw value. Registering a room stores hashes and labels. Direct HTTP or a compromised relay
  can still expose the reusable credential; #304 and #310 own its replacement.

The state directory contains `marks.json` waypoints, the `rooms.json` registry, submitted
`reports/*.md`, and updater status stamps. They survive a restart on purpose. Live member
positions and pings remain memory-only and are not written as movement history.

## Which version everything speaks

Every room reply and `/health` carry `protocol`, a whole number.

Everything on this wire is additive: a new field is optional and an older reader ignores it. So
a mismatch is almost never fatal — which is exactly why it is worth stating. A client quietly
missing a field it was never sent looks identical to a feature that does not work, and there is
no way to tell those apart from inside the application. The day the group key replaced a room
name and a server-side secret, a client that had updated could not talk to a server that had
not, and nothing anywhere said so.

The number goes up only when a change is **not** additive. A new optional field does not raise
it.

A client that sees a different number says so once, on the same line that describes the group:

> Relay speaks 2, this build speaks 3 · it updates itself within half an hour

Neither direction is an error and neither stops sharing. The relay updates itself every half
hour, so a relay behind the client fixes itself; a relay ahead of it means the client is the one
due an update.

## Responses

| Code | Meaning |
| --- | --- |
| 200 | Published; the body is everyone else, plus the group's marks |
| 400 | The display name is missing or longer than 48 characters |
| 401 | The `X-Group-Key` header is missing, or outside 8–128 characters |
| 403 | The relay is closed and this key's room is not one its operator registered |

A 401 does **not** mean a wrong key. There is no such thing here: a key nobody else uses names
a group nobody else is in, and returns 200 with an empty member list. A 403 is the one answer
that does mean the key is wrong for this relay, and only on a relay whose operator has closed it.
