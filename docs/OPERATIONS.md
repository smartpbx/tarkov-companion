# Operations

Where things run, where they write, and what to do when one of them stops.

Everything here is about the running system. How it is built is `TESTING.md`; how the Windows
package is proven is `WINDOWS_VERIFICATION.md`. Diagnostic contracts and response procedures are
in `OBSERVABILITY.md`, `PRIVACY.md`, and `runbooks/relay-operations.md`.

## The desktop application

**Where it installs.** Velopack, per-user, under `%LOCALAPPDATA%\TarkovCompanionDesktop`.
Nothing needs administrator rights and nothing is written to Program Files.

**Where it keeps things.**

| What | Where |
| --- | --- |
| Database | `%LOCALAPPDATA%\TarkovCompanion\tarkov-companion.db` |
| Settings | `%LOCALAPPDATA%\TarkovCompanion\Config` |
| Cached map artwork | `%LOCALAPPDATA%\TarkovCompanion\Cache\Maps` |
| Logs | `%LOCALAPPDATA%\TarkovCompanion\Logs` |

A portable install (`portable.flag` beside the executable) puts all of them under `Data\`
next to the application instead.

**Where it does not keep things.** It never writes inside the game's folders except to move
old screenshots to the recycle bin, and only when that is switched on.

**How it updates.** The anonymous public-release updater has been removed. The signed private-feed
consumer exists but is intentionally not composed until #294 supplies activation and #270 supplies
durable consumer state. Until then an installed build follows the unsigned rough channel the
relay serves at `/updates/rough/` (what it proves and does not: `RELEASES.md`, "The rough
channel"; how to publish to it: `deploy/group-server/README.md`), and signed desktop builds use
the verified offline path in `RELEASES.md`. A build run from a portable zip does not update
itself and keeps its data beside its executable.

## The group relay

| | |
| --- | --- |
| Host | Proxmox CT 115 |
| Address | `10.10.10.80:8090` on the LAN, and a Cloudflare hostname in front of it |
| Unit | `tarkov-group.service` |
| Update timer | `tarkov-group-update.timer`, every 30 minutes |
| State | `/var/lib/tarkov-group` once `StateDirectory=tarkov-group` is set on the unit |

**What it stores.** The state directory holds:

| File | What is in it |
| --- | --- |
| `marks.json` | Every room's waypoints: map, coordinates, label, who placed it, and who reached it and when. Waypoints older than seven days are dropped only when the relay restarts. |
| `rooms.json` | The registered room hashes, with their labels and creation times. |
| `relay-devices.json` | The paired-device registry (v2r-relay-owner): the owner and paired devices' key thumbprints, session/channel ids, and lifecycle audit trail. No private key material and no plaintext bearer credential — only its digest. Checksummed with one independently verified backup; see `VerifiedRelayRegistryStore`. |
| `reports/*.md` | Problem reports exactly as sent, with no expiry. |
| `UPDATE_NOW` | The panel's transient request for the root-owned updater to run. |

Live member state (position, recent trail, kit) stays in memory until about three minutes after
a member stops publishing and is not written to any of those files. Pings expire in forty-five
seconds and are not persisted, because one restored from disk would be claiming "now". The
ordinary desktop report is a closed allowlisted projection, but the relay endpoint still accepts
arbitrary caller-supplied bodies and performs no content allowlisting. Treat `reports/` as
sensitive in backups and migrations (`RISK-REPORT-REDACTION`).

Authenticated update history and install/refusal state live under root-owned
`/var/lib/tarkov-group-update`; the panel reads non-authoritative status copies from
`/var/lib/tarkov-group-update-status`. Neither belongs to the relay's writable state directory.

**How it updates itself.** The timer follows a signed ring in a separate private feed. Before it
touches `/opt/tarkov-group`, the root updater verifies the create-once ring decision, manifest,
and archive against its separately provisioned Sigstore trust root, replay floor, and local
history. A signed rollback is the only normal downgrade authority. If the replacement does not
answer `/health` as its signed identity, the updater restores the previous tree and records the
refusal. See `RELEASES.md` and `deploy/group-server/README.md` for the complete contract.

## The self-test

**Setup → Diagnostics → Run self-test.** It exercises seven capabilities against the installation
it is running on and reports what each one found, one line at a time, with what the line was read
from beside it: the game folders and when each last changed, what this build understood of the
newest game session, a screenshot timed from the game writing it to a position coming out of its
name, every game-data endpoint by name with its size, age and row count, the database's applied
migrations and table counts, the relay's build and round trip and how far behind squadmate
positions are, and which tablets are paired and whether a scene is being published.

Three verdicts. **working** means it was measured and it did what it claims. **not working** means
it was measured and it did not. **could not be tested** means it could not be measured here, with
the reason said out loud — never a pass by default, which is the whole difference between this and
the readiness checklist above it.

It is safe to press at any time, including mid-raid. Every reading is read-only: discovery
re-probes folders, the log and screenshot readers open files for reading with full sharing, game
data and the database are `SELECT`s, and the relay is asked for `GET /health` and nothing else.
Nothing starts a refresh, writes to the game's folders, or changes relay state. The run is bounded
(75 seconds) and **Stop** cancels it.

The one thing it asks for is a screenshot: a position only exists once the game's screenshot key
is pressed, so the panel says so while it waits (45 seconds) rather than reporting a failure the
player could have prevented.

**Copy result** puts the whole thing on the clipboard, paths included — that is local diagnostic
data, which `docs/SAFETY.md` permits, and the folder and endpoint names are usually most of the
answer. **Copy diagnostics** and **Report a problem** carry the same run projected into
`SupportBundle`'s closed schema: one line per capability giving its fixed identifier and its
verdict, and no path, endpoint reason, device name, room member or coordinate. Copy diagnostics,
being local, appends the full text as well.

## Problem reports

A player presses **Report a problem** on Settings. The desktop builds the same closed, bounded
text exposed by **Copy diagnostics**, but currently sends it without a separate confirmation
preview. The relay keeps it in `/var/lib/tarkov-group/reports` and hands back a 12-hex reference.
The hourly `relay-watch.yml` is designed to validate the complete bounded listing and open one
issue per reference without copying the report body.

**The relay holds no GitHub credential.** The workflow files the issues with the token GitHub
Actions already gives it for its own repository, so the internet-facing box never holds a
long-lived token with write access to anything.

**Automated pickup currently fails closed.** `/reports` lists the timestamp-prefixed stored
filename, while `/reports/{reference}` accepts the original 12-hex reference. The workflow
rejects that mismatched shape before writing any issue. #310 owns aligning those two contracts;
an issue is not evidence that retrieval works. If the original 12-hex reference is available,
an authorized operator can read it with:

```bash
curl -H "X-Admin-Key: $TARKOV_RELAY_ADMIN_KEY" https://<relay>/reports/<reference>
```

The admin key is provisioned in two places: the repository secret `TARKOV_RELAY_ADMIN_KEY`, and
`/etc/systemd/system/tarkov-group.service.d/10-reports.conf` on CT 115. Those are not the only
places it exists while in use. The running relay holds it in its environment, the hourly
`relay-watch.yml` job receives it as an environment variable, the shell that runs the command
above holds it, and the relay panel copies whatever is typed into it to that browser tab's
`sessionStorage` for the life of the tab (asset A-6 in `docs/security/ASSETS_AND_ACTORS.md`).
Rotating it means changing both provisioned copies. It is not the group key — any member of any
group holds one of those, and this lists every group's reports.

## The relay panel

`https://<relay>/admin`, with the same admin key typed into the page. It shows which build is
running and how long it has been up, which rooms are registered, and — the row that matters —
which rooms currently have members and are **not** registered.

By default the relay serves any room anybody's key hashes to. Registering the first room closes
it to every other one, so the order is: open the panel, adopt each room that has your friends in
it, then check that the unregistered list is empty. See `docs/GROUP_RELAY.md` for what the three
ways of registering a room mean.

The list is `rooms.json` in the relay's state directory (`/var/lib/tarkov-group`), which is
outside the tree the updater replaces, so it survives the half-hourly update. Deleting that file
reopens the relay; so, silently, does a corrupt or unreadable one (`RISK-RELAY-REGISTRY-FAIL-OPEN`,
#310). After anything touches the state directory, check that the panel still says closed.

The page also says which build is running against which is published, including the case worth
catching: a build that installed, failed its health check and was rolled back is recorded in
`REFUSED_SHA256` and not retried until a newer one is published, so a relay stuck behind for
that reason used to look exactly like one that was up to date.

**Update now** writes `UPDATE_NOW` in the state directory. `tarkov-group-update.path` watches
for it and runs the same update the timer runs — the relay runs unprivileged and cannot start a
unit itself. If that path unit is not installed the button still works, in the sense that the
next timer tick picks the file up; it is just no longer immediate.

A report is untrusted diagnostic content. The ordinary desktop now constructs one closed,
bounded projection and never opens the application log or renders runtime details, screenshot
names, coordinates, paths, credentials, identities, OCR/pixels, or exception bodies. Its hostile
complete-payload fixture proves that client boundary. The relay still accepts arbitrary bodies,
sends no separate confirmation preview, and retains exact submitted bytes, so treat every stored
report as restricted until #281/#310 close and test the complete transport and lifecycle.

**When the group panel says something is wrong:**

| What it says | What it means |
| --- | --- |
| "Needs an https address…" | the key travels on every request; only a local address may use http |
| "The group key must be between 8 and 128 characters" | a 401, which is a length check — not a wrong key |
| "…last heard 42s ago" | the relay stopped answering; the last good picture is still on screen |
| "no position: the game's screenshot folder has not been found" | this companion is in the room and has nothing to put on the map |

A key that is merely *different* is not an error. The key **is** the room, so a typo puts
somebody in a room of their own where everything works and nobody is there.

## When a signed ring is missing or stale

The old public `dev` release is a frozen migration source, not v2 release authority. If the relay
or an offline desktop is behind:

1. Check `publish.yml` for the successful `windows-verify.yml` push-to-main run. Verification
   builds and proves artifacts but cannot publish; the protected release job publishes without
   rebuilding them.
2. Inspect the selected private ring's newest signed, create-once decision and the relay updater's
   root-owned published, installed, and refused records. A paused ring, an authorized rollback,
   a superseded verification run, disabled `V2_RELEASES_ENABLED`, or missing host provisioning are
   different states and must not be collapsed into "stale."
3. Use the transition and recovery procedures in `RELEASES.md`. Do not revive the public `dev`
   writer, overwrite a ring decision, or trust an archive because a checksum beside it matches.

The signed ring decision and signed release manifest, verified against the consumer's own trust
root and history, are the authority. Relay watch intentionally reports only liveness and known
default-branch lineage; release selection and freshness remain updater/admin-panel evidence.

## The fast loop

CT 114 on Proxmox, `/root/repos/tarkov-companion`. The .NET 10 SDK is at `/root/.dotnet` and is
not on `PATH`. A build is about fifteen seconds, the whole suite about twenty.

Its `/root/repos` is a symlink to a path that *looks* like the workstation's
(`/home/cmannerow/Nextcloud/Documents/programming`) and is the container's own disk. Build
output there prints workstation-shaped paths and is not the workstation.
