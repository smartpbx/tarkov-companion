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

One secret, shared between the group, in the `X-Group-Secret` header. Rooms are named by the
group. That is the whole access model: it suits friends and it is not an account system, which
is said plainly here so nobody mistakes it for one.

`GROUP_SECRET` has no default. The server refuses to start without it rather than run open,
because what it relays is people's live positions.

## Running it

```
GROUP_SECRET=<the group's secret> dotnet run --project src/TarkovCompanion.GroupServer
```

Behind a reverse proxy that terminates TLS, which is what the forwarded-header handling
expects. It listens on plain HTTP and should never be exposed directly.

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/health` | Liveness, for the proxy and for a person checking it is up |
| `POST` | `/rooms/{room}/state` | Publish yourself, receive everyone else |
| `DELETE` | `/rooms/{room}/state/{name}` | Leave immediately rather than timing out |

One exchange does both halves, so there is no connection to hold open and no subscription to
leak. A companion that is not running sends nothing and therefore shows nothing.
