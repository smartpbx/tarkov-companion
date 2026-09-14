# Connecting a client to the group relay

Anything that can make an HTTPS request can join a group. This document is the whole protocol.

    https://tarkov.mannerow.net

## The group key

One value, agreed between the people in the group and typed into each of their clients. It is
not published here and it is not set on the server: **the server holds no secrets at all.**

It hashes the key you send and buckets members by the result, so the key is both *which group*
and *proof you are in it*. Type the same key as your friends and you see each other. Type a
different one and you are in a different group, quietly, rather than being refused.

Keys under eight characters are rejected. That is not access control, since there is nothing to
check against; it stops somebody believing `a` keeps strangers out.

The address above is public, which is safe for the same reason: knowing where the relay is does
not get you into a group whose key you do not know, because the room is that key's hash and is
not discoverable from outside.

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

Optional. A member who simply stops publishing disappears after three minutes.

## Health

    GET /health   ->   {"status":"ok"}

No key required.

## Who may have a room

By default, anybody who can reach the relay. A key names a room, so a stranger who reaches the
relay can invent a key and be in one — exactly as they could have invented a room name before
keys replaced room names. What they cannot do is join *your* room, because its name is the hash
of a secret and is not discoverable from outside.

That is enough for a relay nobody else knows the address of, and stops being enough when one
does. So an operator can register the rooms that are meant to exist, from the panel at `/admin`:

- **With nothing registered the relay is open**, and behaves exactly as it always has.
- **Registering the first room closes it.** From then on, a key whose room is not registered is
  refused with a 403 and the client says so rather than showing an empty group.

Registering a room does not mean handing the relay a key. Three ways in, all of which end as the
same stored hash:

| Way | When |
| --- | --- |
| Have a key generated | A new group. The key is shown once, on the page, and nowhere else. |
| Give a key the group already uses | Rooms that predate the list, when you know the key. |
| Adopt a room the relay is holding | Rooms that predate the list when you do not. |

The third is the one that makes closing an established relay painless: the panel lists the rooms
that currently have members and are not registered, and adopting one keeps the friends already
in it. Anything still on that list after you have adopted your own is somebody you did not
invite.

## What the server does not do

- It keeps no history. Where people have been would be easy to record and is deliberately not.
- It has no accounts and no identities beyond the display name you send.
- It never sees your key, only its hash, and never logs either. Registering a room does not
  change that: the list holds hashes and labels.

What it does write is two files, both in its state directory and neither about where anybody has
been: `marks.json`, the waypoints a group placed, and `rooms.json`, the rooms an operator
registered. Both survive a restart on purpose — the relay updates itself every half hour.

## Responses

| Code | Meaning |
| --- | --- |
| 200 | Published; the body is everyone else, plus the group's marks |
| 400 | The display name is missing or longer than 48 characters |
| 401 | The `X-Group-Key` header is missing or shorter than eight characters |
| 403 | The relay is closed and this key's room is not one its operator registered |

A 401 does **not** mean a wrong key. There is no such thing here: a key nobody else uses names
a group nobody else is in, and returns 200 with an empty member list. A 403 is the one answer
that does mean the key is wrong for this relay, and only on a relay whose operator has closed it.
