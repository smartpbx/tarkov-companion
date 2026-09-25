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
- `objectives` — the open objectives of those quests (#780): `[{"task": "<quest id>", "id":
  "<objective id>", "count": 2}]`, `count` optional. At most 60. Ids only, named and placed from
  the receiver's own catalog. A relay older than this field drops it; a client older than it
  ignores it.
- `gameMode` — `"pvp"`, `"pve"` or `"seasonal"`, the sender's active profile (#269). At most 16
  characters. A receiver on another mode drops that member's `quests`, `questIds` and
  `objectives` and says so once on the Team page; absent means unknown, never "different".
- `ready`, `plannedExtract`, `note` — what the player set in Team's Ready check (#289): ready
  or not (absent means not said), an extract (at most 64 characters, sent only while the sender
  is on that extract's map or on none) and a note (at most 120). A relay older than these drops
  them; a client older than them ignores them. A desktop "Squad" mark whose send fails is queued,
  shown as "Queued" in Team's marks, and sent when the group is next seen live (at most 15 min).
- `drawings` — lines the player drew on the Raid map in Draw mode with "Squad" scope (#286):
  `[{"id": "<≤64>", "mapId": "customs", "floor": "<catalog floor id, optional>", "points":
  [x0, z0, x1, z1, …]}]`, world metres rounded to 0.1. At most 20 lines of 2 to 200 points; the
  client also keeps the total under 1,000 points so a full publish stays inside the 32 KB body
  bound. Omitted when nothing is shared. No lifetime crosses the wire: the sender stops
  publishing a line when it expires or is removed, and a line leaves with its member. A relay
  older than this field drops it; a client older than it ignores it.

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

## Being told sooner: holding the exchange open

Calling every few seconds means a squadmate's position waits most of a tick to be published and
most of another to be collected. So the exchange may be held:

    POST /state?wait=5&since=41

- `wait` — seconds you are willing to wait for the room to change. The relay caps it at 20.
- `since` — the `revision` your previous reply carried.

Send both and the relay publishes you as usual, then keeps the reply back until the room changes
or your wait runs out, whichever is first. Send neither, or either alone, and you get the
immediate reply you always got. Nothing else about the request or the reply changed.

The reply carries `revision`, which counts changes made by everybody but you — your own publish
cannot be what ends your own hold, or every wait would finish on the request that started it.
A publish that says nothing new (the same state, only older position, trail or raid-clock ages) is not a change and
wakes nobody; counting it kept every member of a four-person room exchanging three times a second (#453).
Send the last one you saw back as `since`. A reply with no `revision` is a relay that does not
hold; keep to your own tick against it.

What it costs the relay: one socket and one continuation per held request, at most 256 held at
once (past that you are answered immediately), and the hold ends as soon as you disconnect. When
it answers it writes one room, which is 4.4 KB for a five-member squad.

Our client asks for a five-second hold rather than the full twenty, because the tick is also what
keeps presence, position ages and staleness current — and it ends its own hold early whenever the
player's position changes, so a new screenshot goes up at once instead of at the end of the wait.

Its screenshot folder is polled four times a second while a raid is running and sharing is on, and
once a second otherwise, so the poll is no longer most of the wait either. The whole path —
screenshot written to the other member's marker moving — measures 0.40 s median and about 0.45 s
at p95 over a 60 ms link.

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

`by` and `mapId` are required; `label` is optional and is cut at 64 characters. `color` is
optional (#290): one of the six `MarkPalette` colours, or it is dropped; it comes back on the mark
only when set, so older clients see the shape above. `y` is the
height, which matters for a map with floors. The reply is the created mark, including the `id`
the server assigned, so two members marking at once cannot collide.

The V2 desktop sends every ping and waypoint placed on its Raid map (or on a paired tablet) this
way, keeps the returned `id`, and deletes it when the mark is removed, moved or expires (#707).
A "Just me" mark is never sent; a mark whose lifetime is not the 45-second ping is sent as a
waypoint and deleted when it expires locally; if the relay drops one of our waypoints that it was
seen holding, the local copy goes too, with a "removed by squad" note (#289).
A member in `PostRaid` or `Menu` publishes no position or trail, and is not drawn on the map.

### Reaching, removing, clearing

    POST   /waypoints/{id}/reached      { "by": "MaxGooner" }
    DELETE /waypoints/{id}[?by=MaxGooner]
    DELETE /waypoints?mapId=customs&reachedOnly=true

Reaching one records who got there and will not overwrite whoever arrived first. Clearing
returns how many went. Omit `mapId` to clear every map. With `by`, a removal only takes a mark
that name dropped; the desktop sends it when it takes back its own mark (#886). Mark ids start
at the relay's clock in milliseconds, so a restart never hands out an id again.

### Limits

Sixty waypoints and thirty pings per group. Past that the oldest goes, so somebody leaning on a
mouse button loses their stalest plan rather than being refused or filling the server. Marks are
held for at most as many groups as the room cap (a new group past it gets a 503); waypoints older
than seven days and emptied groups are swept every minute (#886).

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

    GET /health   ->   {"status":"ok","protocol":1,"version":"2.0.1140", ...}

No key required. `protocol` is the number described above; `version` and `commit` say which
build is answering. The current response also includes start time and aggregate room/member
counts, so it is not a minimal liveness-only route.

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
map's normalised name. No key: this is public game data, like `/catalog`.

Since #407 the tablet no longer reads this. It draws the desktop's own scene instead (below), which
already carries these places in the plan's units. It remains for the desktop's own naming and for
any other client of this relay.

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
`/landmarks`. Since #407 the tablet does not use this either: a lookup typed on a tablet runs on
the paired desktop, which has the whole catalogue and knows which quests and hideout modules are
asking for the item.

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

The relay state directory contains `marks.json` waypoints, the `rooms.json` registry, submitted
`reports/*.md`, and the transient `UPDATE_NOW` request. They survive a restart on purpose except
for that request, which the updater consumes. Authenticated updater history and status are kept in
separate root-owned directories. Live member positions and pings remain memory-only and are not
written as movement history.

## Paired-device pairing (separate from the group key)

A paired tablet (`docs/PAIRED_DEVICE_PROTOCOL.md`) is one device of one desktop, not a group
member, and does not use the group key. The relay carries only the pairing ceremony's plaintext
messages under `/v2/companion/pairing/*` — an offer, a request, a nonce reveal, a challenge, a
device-key proof, and a session establishment, one attempt at a time, keyed by attempt ID and
swept once the attempt's own offer expires. `CompanionPairingMailbox` is the whole of it: it never
advances the pairing state machine, verifies a signature, or sees a device name, a nonce, or a
traffic key, the same way this file's group routes never see anything the client did not choose to
publish. A code is rate-limited per source the same way `/admin/rooms` gates by key, but consumed
under `NormalizePairingCode`, not `GroupKey`.

This is the pairing hop only. Once a session is established, its own traffic — commands, marks,
capture arming — travels as an `OpaqueRelayFrame` over the routes below (v2r-relay-owner), not
through this mailbox.

## One tenant per desktop (#553)

A relay is a group's, so nobody claims it. Each desktop registers itself and is the owner of its
own tablets and of nothing else:

    POST /v2/companion/relay/possession/challenge      -> a single-use nonce
    POST /v2/companion/relay/desktops/register
    X-Group-Key: <the group key the desktop already holds>
    {...the same self-pairing body as the claim below, built around that nonce...}

The group key is checked exactly as `/state` checks it (`GroupKey.TryRead`, `GroupKey.RoomFor`,
the operator's room list when there is one, and the wrong-key limiter); the signature inside the
body proves the desktop's identity key. A key the relay knows resumes that desktop with its
tablets intact (restart, next day); a new key gets a registry, frame hub, map store and resume
tickets of its own (`RelayTenantDirectory`). Every device-scoped route finds its tenant from the
session id, so a tablet reads only its own desktop's map and a desktop lists and revokes only its
own tablets. A pairing is accepted only into the tenant whose identity key signed it. Bounds: 16
desktops per room, 48 per relay. A tablet coming back names its desktop with `&desktopKeyId=` on
`resume/requests`. The admin key plays no part in any of this.

The section below is the older single-owner protocol. It still works, against the one legacy
registry (`relay-devices.json`), so a desktop from before #553 keeps its tablets on a new relay;
when that desktop upgrades and registers, the legacy registry becomes its tenant and its room is
learned then.

## Claiming the relay's owner (v2r-relay-owner)

`RelayDeviceRegistry` and `OpaqueRelayFrameHub` route a paired session's opaque traffic once a
relay has an owner, but nothing bootstraps that first owner on its own — pairing always needs an
*existing* owner to approve it. An operator claims it once, with the relay's own admin key:

    POST /admin/relay/claim
    X-Admin-Key: <the operator's admin key>
    Content-Type: application/json

    {"offer": {...}, "desktopNonceBase64Url": "...", "codeConsumedUtc": "...",
     "request": {...}, "challenge": {...}, "establishment": {...}}

The desktop builds this body itself (`DesktopRelayOwnerClaim`): a real, self-consistent completed
pairing that names the desktop's own identity key as the device, without a second device to run
the ordinary two-party WebAuthn ceremony against. The relay never re-verifies that proof itself —
for an ordinary pairing it trusts an already-authenticated owner's session to have run it; here,
with no owner yet, the admin key is the entire authorization boundary. It requires
`TARKOV_RELAY_OWNER_RECOVERY_SECRET` (protected operator configuration, standard base64, at least
32 decoded bytes — `openssl rand -base64 32`) to be set alongside `TARKOV_RELAY_ADMIN_KEY`; without
it the route refuses outright with 501, the same fail-closed shape `RelayAdmin` uses. The secret
binds `OwnerRecoveryProtector`'s single-use, two-minute recovery grant to the claiming device's key
thumbprint. The claim route is rate limited per source and relay-wide
(`RelayOwnerClaimGate`), on top of the admin key check.

    GET /admin/relay/owner
    X-Admin-Key: <the operator's admin key>

    {"claimed": true, "ownerDeviceId": "…"}

From the desktop app: **Team → Devices → "Claim this relay"** (package 48 moved this out of its own
popout window and into the workspace; the panel names `TARKOV_RELAY_ADMIN_KEY` where it asks for the
key). The admin key is typed once and never stored; the panel checks status first so a relay already
claimed by another desktop is reported without spending this desktop's own rate-limit budget on an
attempt that can only fail.

The claim is kept (2026-09-20, #289). The session the relay issues is stored in the desktop's
protected secret store (DPAPI, beside the TarkovTracker token) and picked back up at startup, so the
panel reads claimed after a restart with nothing typed; "Forget this relay" drops it.

The desktop measures the relay's HTTP `Date` header, names clock skew in Team and Diagnostics, and
retries a refused registration after 30 s, 1 m, 2 m, 4 m, then every 5 m (#704). A clock set while
the app runs (#799) is noticed within 2 s by comparing wall and monotonic time; the raid's held
times and the marks' creation and expiry move by the jump, and the group publishes at once.

The admin key is typed once per machine, not once per day (2026-09-20, #289). A session still lives
twelve hours and an owner two idle, and the owner-recovery rule is unchanged for anybody holding
only the admin key: it never replaces an owner the relay still counts as live. What the relay can
now do is recognise the key holder. The owner's public key is on record from the first claim
(`relay-devices.json`); `POST /v2/companion/relay/possession/challenge` hands out a single-use
nonce, and `POST /v2/companion/relay/owner/resume` takes the same self-pairing body as the claim
route, built around that nonce and signed by the desktop's identity key. A body whose signing key
is the one on record is accepted at any time — expired, idle or live — replaces that owner's own
session, and keeps every paired device; any other key gets `owner-key-mismatch` (or
`owner-unknown`, `challenge-rejected`) and is left with the admin key. The desktop does this at
startup and whenever its session is refused, and after "Forget this relay" on pressing Claim.

A paired tablet comes back the same way. It signs the relay's nonce (hashed under
`TarkovCompanion.PairedDevice/v2/relay-resume-door`, never bare) with its device key at
`POST /v2/companion/relay/resume/requests?deviceKeyId=…`; the relay checks that against the key it
holds for that device and refuses `device-unknown`, `device-revoked` or `proof-rejected`, and
`409 desktop-offline` when that device's desktop has not been heard from for two minutes (#693:
nobody could answer the ticket, and the tablet said "Reconnecting" indefinitely). A ticket
is all it gives: the owner sees it on its next frames read (`resumeRequests`), opens an ordinary
pairing offer, answers the ticket with the offer's code
(`POST …/resume/requests/{ticket}/offer`), and the pairing handshake runs with the code entry and
the six-digit comparison left out, both long-term keys being already pinned. A revoke now also
marks a device that had merely expired, so going quiet is not a way around being revoked.
The tablet keeps a confirmed **Pair again** escape visible throughout this wait, and a fresh QR
link always starts its new pairing attempt instead of restoring the remembered desktop (#708).
The code form stays on screen while it reconnects, each pairing request gives up after 10 s, a
failed or unanswered mailbox poll is asked again, and a returning tablet stops waiting on a step
after 30 s (#840). A desktop that will not have a returning tablet back says so (#846): the owner posts
`POST …/resume/requests/{ticket}/refusal?reason=not-recognised|failed`, the ticket read carries
`refused`, and the page shows the code form at once. An older page ignores the field and times out.
`POST /v2/companion/relay/devices/{deviceId}/revoke` (owner session) is how a desktop's Revoke
reaches the relay, and an owner registering a tablet whose device key is already known replaces
that tablet's old record.

The route answers **501 before it reads the admin key** when `TARKOV_RELAY_OWNER_RECOVERY_SECRET` is
unset, because without it no owner can ever be recovered. The desktop reports that as its own state
rather than as a refusal to retry — nothing a person does at the keyboard fixes it, only the
operator setting the secret.

Refusals name their cause rather than sharing one code: `claim-not-completed`,
`claim-grant-mismatch`, `owner-already-live`, `recovery-grant-rejected`. An establishment is
timestamped on the desktop, so it is compared against the relay's clock with the protocol's
one-minute skew allowance — requiring the two clocks to agree exactly refused every claim from a
desktop a fraction of a second ahead (package 48). Outside that allowance the claim routes answer
`{"code":"clock-skew","offsetSeconds":-14400}` (server minus claim time); every other refusal
keeps its existing string code body.

Once claimed, the owner registers each paired tablet on the relay too (separately from the
desktop's own local `DesktopCompanionAuthority` record of it), bearer-authenticated with the
session `/admin/relay/claim` returned:

    POST /v2/companion/relay/devices
    X-Relay-Session: <session id>
    X-Relay-Credential: <bearer secret>

    {"pairing": {...same five fields as the claim body...}, "role": "Member", "surface": "TabletLandscape"}

and from then on both sides exchange `OpaqueRelayFrame`s the same way:

    POST /v2/companion/relay/frames        (publish one frame)
    GET  /v2/companion/relay/frames?after=<deliveryId>[&wait=<seconds>]
    POST /v2/companion/relay/frames/{deliveryId}/ack

A frame read that names `wait` (#604) is held until a frame is queued (at most 20 s, 256 held
reads), acknowledges everything through its own `after`, and is answered with
`X-Relay-Frames-Held: 1`; a reader that names no `wait` is answered and acknowledges as before.

Bearer headers rather than a cookie, because the caller is always a native `HttpClient` the
desktop or tablet code sets explicitly, never a browser attaching an ambient credential — so CSRF,
which only defends against that ambient attachment, does not apply here.

**First live payload: marks.** A mark a tablet places or removes reaches the desktop's own local
mark store (`IRaidMarkStore`) through this transport: the tablet's `UpsertMarkCommand`/
`DeleteMarkCommand`, sealed inside a `ClientCommandEnvelope` by `Tablet/relay-crypto.js` (a byte-
exact port of `PairingCryptography`'s nonce/AAD encoding and AES-256-GCM sealing, checked against
`Golden/crypto`'s independent vector and a real cross-language round trip — see
`RelayCryptoInteropTests`), is applied through the existing, already-tested
`DesktopCompanionAuthority.ApplyCommandAsync`, and `RelayMarksBridge` reconciles the result into
the desktop's map. The tablet fetches its own bearer secret from the same bounded pairing mailbox
`established` came through (`POST`/`GET /v2/companion/pairing/relay-session/{attemptId}`), since
the desktop only learns it from `POST /v2/companion/relay/devices`'s response after `established`
has already been handed over. The other direction — a mark the desktop already had, or places
locally, reaching the tablet — is still deferred; see the v2r-tablet-marks-sync package PR's
"Deferred to polish".

## The desktop's map, for its paired tablets (#407)

A tablet is only ever a companion paired to one desktop, and a tablet without the real map is not
usable. The desktop is the side that has one — it holds the reviewed artwork, the plan rectangle
(`MapPlanProjection`) and the assembled `MapSceneSnapshot` — so it publishes what it is drawing and
its tablets read it back:

| Route | Who | What |
| --- | --- | --- |
| `POST /v2/companion/relay/map` | the relay's owner | The scene as JSON (`TabletMapSurface`): the plan rectangle, the layers, every object already in that rectangle's units, the desktop's camera, the reviewed asset's attribution, and which artwork it expects. At most 1 MiB. |
| `POST /v2/companion/relay/map/artwork?sha256=…` | the relay's owner | The picture itself, `image/png`/`jpeg`/`webp`, at most 24 MiB. The relay hashes the body and refuses it unless it is the bytes the caller declared. |
| `GET /v2/companion/relay/map` | any live paired session | The current scene, or 404 while nothing is published. |
| `GET /v2/companion/relay/map/artwork` | any live paired session | Its bytes, with the content hash as the ETag. |

Both reads take the same `X-Relay-Session`/`X-Relay-Credential` pair as the frame routes, so a
revoked device reads nothing: its credential stops authenticating. Nothing here is public — a
reviewed asset served to anybody who asked would be a redistribution its licence does not cover.

Why not a sealed frame, when everything else after pairing is one? A relay payload root is bounded
at 64 KiB and a rasterized plan is megabytes. The relay holds this one opaquely: it never parses
the scene and never learns which map it is. The artwork is uploaded only when its content hash
changes.

Whether the desktop is there is the relay's to say, not the map's age (2026-09-20, #407): every
answer to a map read, found or not, carries `X-Relay-Owner-Seen-Ms`, the time since the owner last
made any authenticated call, and the desktop reads its queue every two seconds. A desktop sitting
still on one map publishes nothing, and a tablet judging by the scene's timestamp called that
offline after twenty seconds; it now says so after fifteen seconds of owner silence, and falls
back to the timestamp only against a relay that sends no such header. The map is memory-only on
the relay, so each owner read of `/v2/companion/relay/frames` also carries
`map: {held, revision, artworkSha256}`, and a desktop that finds the relay holding nothing (a
restart, or a map published before the claim) or holding another picture uploads again.
`POST /v2/companion/relay/frames/reset` clears a queue that reported `requiresReconnect` and
answers `{after}`, the cursor to read from next; without it the flag never cleared.

**The read is held, not polled.** `GET /v2/companion/relay/map?since=<revision>&wait=<seconds>`
waits until the desktop publishes something newer than the revision the tablet already has, or
until the wait runs out — the same shape the group exchange uses, the same twenty-second cap, the
same global bound of 256 held requests past which a caller is answered immediately rather than
refused, and the same rule that a caller naming neither parameter is answered exactly as before.
The revision travels in the `X-Relay-Map-Revision` response header rather than in the body,
because the body is the desktop's own bytes and the relay never parses them. `/health` reports
`heldTabletReads`.

Both compatibility directions work untouched: an older tablet page names neither parameter and is
answered at once; an older relay ignores both and sends no revision header, and a newer page falls
back to its timer when the header is absent. The desktop publishes when its scene actually changes
— coalesced over a 40 ms floor, and a tick that changes nothing never starts the clock — rather
than on the fixed one-second throttle it used before.

**Follow, Control and Independent** need no route of their own. They are the same revisioned
commands over the same sealed frames: `SetInteractionModeCommand` for Follow and Independent,
`RequestControlCommand` answered by the desktop's `ResolveControlCommand`, then
`ControlWorkspaceCommand` while the lease lasts, and `PreemptControlCommand` when the desktop takes
control back. The desktop's own navigation travels as `UpdateDesktopWorkspaceCommand`, which is
what a following tablet mirrors. A workspace update names the device whose command caused it, so a
tablet drops the echo of its own change rather than following it.

While it holds Control, the tablet also offers Loot, Stash, and Flea capture arming. Each is a
`RequestCaptureIntentCommand` over the same sealed, capability-checked frame path; an Applied
command reaches the desktop's ordinary capture-arm event with its session and context intact.
This does not capture pixels or generate game input. The tablet page is embedded in the group
server binary, so deploying this interface requires redeploying the relay.

The tablet can relabel and move paired marks, use arrow/zoom/workspace keyboard shortcuts, and
keeps failed new-mark sends in a visible device-local queue for at most fifteen minutes; after a
reconnect it previews each against current canonical state and submits it with a stable command id.
A tablet mark carries the player's scope (`Private` for Just me, `PairedDevice` for Squad; Team
needs a capability no tablet holds) and an optional `lifetime`; a route is up to twelve waypoints
sharing a `routeId`, each with its `routeStep`, drawn on the desktop as one dashed line (#289, #290).
Its `color` is a palette colour when the player chose one (anything else means the kind's own), and
the map surface carries it back on each object; the surface also carries the last Stash scan and
flea screen (`stash`, `flea`: at most 30 rows of 140 characters) for read-only review (#290).

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
| 429 | Too many keys have been refused from this address recently; `Retry-After` says how long |
| 503 | The relay could not read the list of rooms its operator registered, so it is serving none |

A 401 does **not** mean a wrong key. There is no such thing here: a key nobody else uses names
a group nobody else is in, and returns 200 with an empty member list. A 403 is the one answer
that does mean the key is wrong for this relay, and only on a relay whose operator has closed it.

A 429 is what a wrong key costs, added under #317. Nothing counted refused keys, so an eight
character key could be guessed at whatever rate the relay could answer. A caller is now delayed
from its fifth refused key, doubling to two seconds, and refused for sixty seconds after twenty
refusals in five minutes; one accepted key clears the record. It applies to the operator's own
`/admin` and `/reports` as well, on the same ladder. Sixty seconds rather than longer because this
counts by transport address, and a relay behind a reverse proxy sees the proxy: a long lockout
there would be an outage for everybody caused by one stranger. Since #819 a proxy named in
`TARKOV_RELAY_TRUSTED_PROXIES` supplies the caller's address instead (see the deploy README).

A 503 on a group path is a state the relay used to have no way to report. An unreadable
`rooms.json` emptied the list, and an empty list means open — so a closed relay quietly started
serving every room anybody could invent. It refuses everything instead and says which of the two
it is doing, so an operator looks at the file rather than at their key. Restoring or deleting the
file and restarting fixes it, and so does registering a room from the panel, which rewrites it.
