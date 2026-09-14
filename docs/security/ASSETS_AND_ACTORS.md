# Assets and actors

## Assets

Each asset names the boundary (from `SYSTEM_AND_TRUST_BOUNDARIES.md`) where it is created or
first crosses a trust line, its classification, and the source this pass verified it against.

| ID | Asset | Classification | Where it lives | Verified against |
| --- | --- | --- | --- | --- |
| A-1 | Player's own raid/quest/profile history | Personal, local | SQLite DB, `%LOCALAPPDATA%\TarkovCompanion` | `docs/OPERATIONS.md`, `docs/DATABASE.md` |
| A-2 | Other players' data embedded in EFT's own logs (party nicknames, ids, levels, loadouts, dogtag killer/victim names) | Personal (third-party), local-only, never transmitted | Read from game log files; never persisted verbatim beyond permitted scope | `docs/SAFETY.md` §"Other players' data in the game's own logs" |
| A-3 | Screenshot filenames (position, heading) and pixels | Personal, local, capture bytes discarded by default | In-memory during a scan; not retained unless Debug Capture is on | `docs/ARCHITECTURE.md`, `AGENTS.md` |
| A-4 | TarkovTracker bearer token | Secret | DPAPI `CurrentUser`-protected file under `Secrets/` | `WindowsDpapiSecretStore.cs` |
| A-5 | Group key | Shared secret (per group) | Typed by the player each session; the relay stores only its SHA-256, truncated to 32 hex chars, never the key | `GroupKey.cs`, `docs/GROUP_RELAY.md` |
| A-6 | Relay admin key (`TARKOV_RELAY_ADMIN_KEY`) | High-value secret | GitHub repo secret; `/etc/systemd/system/tarkov-group.service.d/10-reports.conf` on CT 115 | `docs/OPERATIONS.md`, `RelayAdmin.cs` |
| A-7 | In-room member state (position, heading, loadout, quests) | Shared within one group, medium sensitivity | Relay memory only, forgotten after 3 minutes of silence; never written to disk | `docs/GROUP_RELAY.md`, `docs/ARCHITECTURE.md` |
| A-8 | Waypoints and pings | Shared within one group, low sensitivity, ownerless by design | Pings: relay memory, 45s TTL. Waypoints: `marks.json` on relay disk, survives restarts | `docs/GROUP_RELAY.md`, `docs/OPERATIONS.md` |
| A-9 | Room registry (which rooms may use a closed relay) | Operational, low sensitivity (hashes + labels, no keys) | `rooms.json` on relay disk | `docs/OPERATIONS.md`, `docs/GROUP_RELAY.md` |
| A-10 | Problem report bodies | Personal (describes a player's machine), redacted before send | `reports/*.md` on relay disk, readable only with the admin key | `ProblemReports.cs`, `docs/OPERATIONS.md` |
| A-11 | Release artifacts and their checksums | Supply-chain critical | GitHub Releases `dev` channel | `README.md`, `docs/OPERATIONS.md` |
| A-12 | Third-party game-data catalog (tarkov.dev, the-hideout) | Public but integrity-relevant — feeds gameplay decisions | Cached locally and on the relay | `docs/DATA_SOURCES.md`, `CatalogMirror.cs` |
| A-13 | GitHub Actions token used by `relay-watch.yml` | Secret, scope-limited to this repository | GitHub-provisioned, ephemeral per run | `docs/OPERATIONS.md` |

## Actors

| ID | Actor | Trust level | Capability | Notes |
| --- | --- | --- | --- | --- |
| ACT-1 | The player | Trusted (their own machine, their own data) | Full control of their own desktop install | The only actor with legitimate access to A-1, A-3, A-4 |
| ACT-2 | Squadmate (party member the player has already met in-game) | Semi-trusted, permitted per `docs/SAFETY.md` | Reads what the player already sees in the raid | Distinct from ACT-3 — this is the "permitted" case in `docs/SAFETY.md`'s log-data rule |
| ACT-3 | A stranger described in the player's own EFT logs (never encountered, or an enemy) | Untrusted, explicitly prohibited from aggregation | None — `docs/SAFETY.md` forbids using this data at all | The boundary the enemy-tracking prohibition exists to enforce |
| ACT-4 | Group relay member holding a valid group key | Semi-trusted within one room | Publishes/reads `/state`, drops/clears any mark in the room (ownerless by design) | Can rename to collide with another member's display name — see ABUSE-RELAY-NAME-COLLISION |
| ACT-5 | Anonymous network client reaching the relay, no key or an invented one | Untrusted | On an open (unregistered) relay: can create a room by inventing a key. On any relay: can call `/health`, `/catalog`, `/landmarks`, `/search` with no key at all | `RelayAccess.IsGroupPath` intentionally excludes these public endpoints from the closed-relay gate |
| ACT-6 | Relay operator | Trusted for their own relay | Reads every group's report bodies, registers/adopts rooms, requests an update | Holds A-6; `docs/OPERATIONS.md` documents this as a distinct, more powerful role than any group key |
| ACT-7 | Compromised or malicious upstream data source (json.tarkov.dev, the-hideout, api.tarkovtracker.org) | Untrusted content, trusted transport (TLS) | Could serve manipulated catalog/map/progress data over an otherwise-valid HTTPS connection | No content-integrity pinning beyond TLS is documented for the catalog/landmark fetches — see CONTROLS_AND_RESIDUAL_RISK RISK-CATALOG-INTEGRITY |
| ACT-8 | Attacker controlling or spoofing the release/update channel | Untrusted, high-impact if successful | Would need to compromise GitHub Releases or DNS/TLS to the release host, since checksums are verified against the published `SHA256SUMS` file | Both the desktop and relay updater depend on this channel's integrity (TB-10) |
| ACT-9 | Compromised tablet device (a squad member's phone/browser, or a device the group key leaked to) | Untrusted once compromised, otherwise same as ACT-4 | Same room-scoped read/write as any relay member holding that key | The key is the only credential; there is no per-device revocation short of rotating the group's key |
| ACT-10 | Same-Windows-user local malware | Untrusted, high local privilege | Can call `CryptUnprotectData` in-process to recover A-4 exactly as the desktop app does | Named residual risk, not mitigated by DPAPI's `CurrentUser` scope — see RISK-DPAPI-SAMEUSER |

## Actor ↔ boundary matrix

Which actor is the relevant threat source at each trust boundary; used to keep abuse cases from
drifting onto an actor that boundary cannot actually see.

| Boundary | Primary actor(s) of concern |
| --- | --- |
| TB-1 EFT ↔ Desktop | ACT-1 (accidental), the project's own future code (the boundary this review exists to keep closed) |
| TB-2 Filesystem ↔ Desktop | ACT-10 |
| TB-3 Desktop ↔ public data | ACT-7, ACT-8 (update path specifically) |
| TB-4 Desktop ↔ Relay | ACT-4, ACT-5 |
| TB-5 Relay ↔ upstream/self-update | ACT-7, ACT-8 |
| TB-6 Player ↔ Squadmate | ACT-2, ACT-4 |
| TB-7 Player ↔ Tablet | ACT-9 |
| TB-8 Operator ↔ Relay | ACT-6, ACT-5 (attempting to reach admin routes without the key) |
| TB-9 Relay ↔ Actions | ACT-6 (report content quality), ACT-5 (report flooding) |
| TB-10 Build ↔ artifact | ACT-8 |
