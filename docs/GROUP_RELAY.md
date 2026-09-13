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
  "quests": ["Debut"]
}
```

Everything except `name` may be null or omitted. `name` is required, is at most 48 characters,
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

## Leaving

    DELETE /state/{name}
    X-Group-Key: <the group's key>

Optional. A member who simply stops publishing disappears after three minutes.

## Health

    GET /health   ->   {"status":"ok"}

No key required.

## What the server does not do

- It stores nothing on disk. Restarting it forgets everyone.
- It keeps no history. Where people have been would be easy to record and is deliberately not.
- It has no accounts, no rate limiting and no identities beyond the display name you send.
- It never sees your key, only its hash, and never logs either.

## Responses

| Code | Meaning |
| --- | --- |
| 200 | Published; the body is everyone else |
| 400 | The display name is missing or longer than 48 characters |
| 401 | The `X-Group-Key` header is missing or shorter than eight characters |

A 401 does **not** mean a wrong key. There is no such thing here: a key nobody else uses names
a group nobody else is in, and returns 200 with an empty member list.
