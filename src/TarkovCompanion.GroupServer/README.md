# Group server

A small relay so a group of friends can see each other on one map during a raid.

It holds nothing on disk. Each member publishes their own state, the server keeps the last
thing each of them said in memory, and hands back everyone else's. A member who stops
publishing disappears after three minutes, and restarting the server forgets everyone. Keeping
a history of where people have been would be easy and is the thing worth not doing.

## What is shared

Only what the sender publishes about themselves: their display name, which map they are on,
their raid state and side, the position and heading from their own screenshot with its age,
their loadout and the quests they are working on.

This is a deliberate departure from the desktop application's usual promise that nothing from
the game's logs leaves the machine. It happens only when somebody turns it on, and what is
sent is listed in `GroupContracts.cs` in full so the promise can be read rather than trusted.

## Access

One value: the group key. Whoever types the same key is in the same group.

The server holds no secrets and has nothing to check a key against. It hashes what it is given
and buckets members by the result, so a key nobody else uses names a room nobody else is in
rather than being refused. The key itself is never stored, never logged, and never leaves the
member's machine in readable form.

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
| `POST` | `/state` | Publish yourself, receive everyone else |
| `DELETE` | `/state/{name}` | Leave immediately rather than timing out |

Both carry the group key in an `X-Group-Key` header. The room is not in the URL because the
key decides it.

One exchange does both halves, so there is no connection to hold open and no subscription to
leak. A companion that is not running sends nothing and therefore shows nothing.
