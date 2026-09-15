# Releases

How a verified build becomes something a relay or a desktop may install, who may move it
between rings, and how to get back. Issue #280 owns this; the threat entries it answers are
`RISK-UPDATE-CHANNEL-TRUST` and `RISK-RELAY-UPDATE-STATE` in `docs/security/`.

This page separates what the repository implements from what an operator has to configure in
GitHub and on hosts. Most of the second list is not configured today (see
[Controls this repository cannot enforce](#controls-this-repository-cannot-enforce)); nothing
here claims otherwise.

## The shape of it

```mermaid
flowchart LR
  PR[Pull request] -->|CI, Windows verification,<br/>License lock| MAIN[Protected main]
  MAIN -->|push| VER[Windows verification<br/>builds, proves, uploads]
  VER -->|workflow_run: push, success| CAND[candidate<br/>gates, reconcile]
  CAND --> SBOM[sbom<br/>no .NET, pinned Syft]
  CAND --> SMOKE[relay-smoke<br/>runs the package, no credentials]
  SBOM --> REL
  SMOKE --> REL[release<br/>ring environment<br/>sign, attest, publish]
  OP[Operator dispatch on main] --> REL
  REL -->|draft, verify digests, publish| BUILD[(Private feed:<br/>v2-build-VERSION)]
  REL -->|create-once file| RING[(Private feed:<br/>rings/RING/release-index-gN.json)]
  RING --> RELAY[Relay updater]
  BUILD --> RELAY
  RING --> DESKTOP[Desktop signed-feed consumer]
  BUILD --> DESKTOP
  BUILD --> MEDIA[Offline media]
```

- **Verification never publishes.** `windows-verify.yml` builds, tests, launches, packages and
  uploads artifacts to its run. The legacy write-capable `dev` job has been removed; only
  `publish.yml` can mutate release or tag state.
- **Publication never builds.** `publish.yml` accepts only what one successful push-to-main
  verification run uploaded. It fetches each artifact archive by the sha256 GitHub recorded at
  upload, and rebuilds nothing.
- **Trust is anchored in a signature, not in the feed.** Every artifact, manifest and ring
  decision is signed keylessly by `publish.yml` at `refs/heads/main` in this repository. A
  consumer verifies against a Sigstore trust root it holds itself, with a pinned cosign.

What that does and does not buy, precisely:
- Whoever controls the feed cannot make a consumer accept bytes the publisher never signed.
- They can withhold new decisions, and delete newer ones so that an older signed decision is the
  newest a consumer sees. A consumer's own history refuses that. A host with no history is
  protected only by a provisioned floor, or not at all ([A host with no history](#a-host-with-no-history)).
- Offline media with `--break-glass` / `-BreakGlass` install any publisher-signed build, without
  ring policy.

## Decision: a private GitHub repository as the feed

Recorded as [ADR 0011](adr/0011-signed-private-release-feed.md); the short form follows.

The v2 feed is a separate private (or internal) GitHub repository chosen per environment through
`V2_RELEASE_REPOSITORY`. The public source repository's Releases page is not a v2 feed, and the
publisher and relay updater both refuse it by name and by visibility.

| Option | Why not |
| --- | --- |
| Keep the public `dev` release | Anonymous, mutable, checksum from the same place as the bytes: exactly `RISK-UPDATE-CHANNEL-TRUST`. |
| Self-host the feed on the relay host (CT 115) | Couples update availability to the thing being updated, puts release storage behind the group's Cloudflare tunnel, and adds a service to operate and patch. |
| Object storage or a CDN | New credentials, new access model and new audit trail to build, for a group of a handful of people. |
| **Private GitHub repository** | Access control with fine-grained, read-only tokens per consumer; commit history as an audit trail of every ring decision; immutable releases for builds; no new infrastructure. |

Consequences accepted with it:
- GitHub availability is a dependency; offline bundles are the recovery path.
- A GitHub account compromise can withhold or delete releases, but cannot forge a signature.
- Consumers need a read-only credential.
- Public-good Sigstore records artifact digests and the source workflow identity in its
  transparency log. For a public source repository that is already public information.

## What a release is

A release is one **signed manifest** (`release-manifest.json`) naming every file with its role,
sha256 and size. `publish.yml` produces it with `scripts/release/build_manifest.py`, from the exact
artifacts of one verification run, and refuses unless all of these agree:

| Checked | Against |
| --- | --- |
| every file verification produced | a file inside an artifact archive whose sha256 equals the digest GitHub recorded when the verification run uploaded it (`artifacts.py`) |
| `update.json` version, commit, run, branch, build time | the selected verification run and its commit on main |
| Portable zip, relay archive, every Velopack file | their checksum files (a Windows runner's `*/d/a/.../dist/…` names included) |
| `BUILD_INFO.txt` inside the package | `update.json` |
| `TarkovCompanion.dll` and `TarkovCompanion.GroupServer.dll` | the informational version `VERSION+COMMIT` compiled into each |
| Velopack full and delta packages, `releases.win.json`, `assets.win.json`, `RELEASES` | package id `TarkovCompanionDesktop`, the release version, and each other's digests and sizes |
| Notices and inventory shipped in the package | `docs/THIRD_PARTY_NOTICES.md` and `docs/THIRD_PARTY_INVENTORY.json` at that commit (line endings aside) |
| Updater script and units shipped in the relay archive | `deploy/group-server/` at that commit, byte for byte |
| The relay's `/health` after extracting the archive | version, commit and relay protocol |
| Database schema, relay protocol, v2 contract, quest exchange | recorded from that commit's source |
| Versioned data component | the release identity and the same four contracts from that commit |
| Versioned OCR model component | the reviewed `eng.traineddata` in that commit, byte for byte |
| The version itself | one SemVer grammar, shared by the manifest builder, ring policy, feed tags, the relay updater and the offline installer |

Any file in the payload that no checksum file or publisher step accounts for is refused, so
nothing is signed into a release without having been named. The manifest's `source` records the
verification run, its attempt, and each uploaded artifact archive with its recorded digest.

The manifest also carries non-empty, authenticated `feeds.binary`, `feeds.data` and
`feeds.model`. Verification emits a deterministic versioned data-contract document and the
reviewed English OCR model; the manifest builder rejects either if it differs from the verified
commit. Binary, data and model artifacts share one manifest and one ring decision, so pause,
rollback and last-known-good cannot apply to one and not the others. No feed has been enabled or
published yet, so this describes the bytes the first release will contain, not a deployed claim.

Delta packages are signed and recorded when verification produces them. The desktop consumer
understands an authenticated `baseSha256` for data/model deltas, stages one only when its
persisted component digest matches, and always requires the signed full artifact as fallback;
Velopack performs the equivalent full fallback for binary deltas. Verification currently emits
full artifacts only, because producing a delta needs the previous release as an input (#279).

Real-payload check: the artifacts of verification run
[34910997075](https://github.com/smartpbx/tarkov-companion/actions/runs/34910997075) (1.0.608,
`cbaf3df`) reconcile, and their relay archive answers `/health` as 1.0.608/`cbaf3df`/protocol 1.
That check ran on the development host against a clean export of that commit, not in CI. For run
[34924256898](https://github.com/smartpbx/tarkov-companion/actions/runs/34924256898), the sha256 of
each archive downloaded from `GET /actions/artifacts/{id}/zip` equals that artifact's recorded
`digest`, which is what `artifacts.py` relies on.

## Publication, step by step

`publish.yml` has five jobs, holding deliberately different authority. Every job refuses any ref
but `refs/heads/main` before it does anything. A `workflow_run` event always runs the workflow file
from the default branch. A `workflow_dispatch` runs the file at the ref it was dispatched from, so a
branch's own copy could run. That copy would sign as that branch, and every consumer refuses that
certificate. The environments' main-only deployment policy would stop it earlier, but that policy
is a repository setting, recorded below as not yet in place.

1. **`request`** (no permissions): resolves a `workflow_run` into *publish to canary*. The
   triggering run must be a successful `push` to `main` whose head repository is this one; a
   fork's pull request from a branch it named `main` stops here. For a dispatch it validates the
   operator's action, ring, and a reason of 1–500 characters. Inputs reach shell only through the
   environment.
2. **`candidate`** (`actions: read`, `contents: read`, no secrets):
   - requires the triggering run, read again from the API, to be `windows-verify.yml`, event
     `push`, branch `main`, this repository, completed successfully, at a commit still reachable
     from main;
   - requires `ci.yml` to have succeeded for the same commit, waiting up to 40 minutes for it;
   - fetches the artifacts by recorded digest and reconciles them;
   - restores that commit's graph and refuses any known vulnerable package at release time. An
     audit that could not reach its sources counts as a failure, not a clean result;
   - requires the locked inventory and third-party notices to match the dependency graph
     (`scripts/audit-licenses.sh`) and runs the secret scan on that commit.
3. **`sbom`** (read-only, no secrets, **no .NET**): fetches the artifacts by recorded digest,
   unpacks both archives and generates an SPDX SBOM with Syft 1.51.1, installed by sha256. The
   restore in `candidate` runs third-party MSBuild logic, so the SBOM is made where none ran.
   Measured on run 608's artifacts: the unpacked scan names 86 NuGet packages, and a scan of the
   packed archives names none. An SBOM without NuGet package URLs is refused.
4. **`relay-smoke`** (read-only, no secrets): fetches the relay archive by recorded digest, extracts
   it and runs it under an emptied environment until `/health` answers as the release. It executes
   the thing being released, so it holds nothing worth stealing.
5. **`release`** (the ring's environment; `id-token: write`, `attestations: write`; the feed
   token only in its final step):
   - installs cosign v3.1.3 by sha256;
   - fetches the artifacts by recorded digest again and re-derives the manifest itself. It uses
     the SBOM only after its digest matches the one `sbom` reported, and the smoke job's observed
     identity only through the manifest builder's own check;
   - signs every artifact and the manifest, and immediately verifies each with the consumer rule;
   - attests [provenance bound to the verification run](#verified-build-provenance), and the
     SBOM, for every artifact digest;
   - runs `scripts/release/transition.sh`, below.

### Verified build provenance

The provenance attestation is an explicit SLSA v1 predicate written by `scripts/release/provenance.py`.
An attestation made without a predicate describes the run it is made in, and the release job
builds nothing.

| Field | Value |
| --- | --- |
| `runDetails.builder.id` | `https://github.com/smartpbx/tarkov-companion/.github/workflows/windows-verify.yml@refs/heads/main` |
| `runDetails.metadata.invocationId` | the verification run and attempt |
| `buildDefinition.resolvedDependencies` | the commit it built (`gitCommit`), and each artifact archive it uploaded with GitHub's recorded sha256 |
| `runDetails.byproducts` `attested-by` | the publishing run and its workflow ref, which observed those facts through the GitHub API and built nothing |

The attestation is signed by `publish.yml`, not by the verification workflow. Its certificate says
so, and the predicate is that workflow's statement about what the verification run produced,
checked against GitHub's own records. Signing provenance inside the producer would need
`id-token: write` in `windows-verify.yml`, which is #279's workflow.

### Writing to the feed

`transition.sh` first requires the feed, read live, to be private or internal and to enforce
immutable releases (`GET /repos/{feed}/immutable-releases`). That read needs the token's
Administration read permission on the feed, and a token without it fails here. It then reads the
ring, verifies its newest decision, asks `release_policy.py` for the next one, signs it, packs
payload and bundle into one envelope, and verifies that envelope. Only then does it write:

- **The build**, for `publish`: `v2-build-VERSION` is created as a **draft** and every file and
  bundle is uploaded. GitHub's own sha256 for every asset is compared with the manifest, and only
  then is the draft published. GitHub must then report the release `immutable`, and the digests
  are compared again. Nothing names a draft. A draft left by an interrupted run is deleted and
  replaced. A *published* build from an interrupted run is adopted only if GitHub reports it
  immutable and its manifest verifies and describes the same verified bytes; otherwise it is
  refused.
- **The decision**, last: `rings/RING/release-index-gNNNNNNNNNN.json` is created through the
  contents API without a parent blob. Creating a path that already exists is refused, so two
  writers who both read generation N cannot both write N+1. The loser gets a conflict, re-reads,
  recomputes against the winner's decision, re-signs and tries again, up to three times.

Release assets were not used for the decision on purpose: `gh release upload --clobber` deletes
the old asset before uploading its replacement, which is an empty feed for as long as the upload
takes, and for ever if it fails. Old decisions are pruned only after the new one reads back, and
never the newest 200. So pruning cannot move a ring backwards or empty it for a reader that lists
the directory. It does make those decisions unavailable to a host that has not yet seen them.

A verified build older than canary's high-water mark is **superseded**: the run reports it and
changes nothing. That is the normal result of two merges verifying out of order.

## Rings

| Ring | Receives | May move to |
| --- | --- | --- |
| canary | every successful protected-main verification | beta, by promotion |
| beta | canary's current signed release | stable, by promotion |
| stable | beta's current signed release | nowhere |

Every transition increments the ring's generation. The signed decision records the previous
generation number, the action, the actor, the reason and the workflow run. Generations are
monotonic numbers, not a hash chain: a decision does not carry a digest of the one before it.

| Action | Effect | Refused when |
| --- | --- | --- |
| `publish` (automatic) | canary serves the new build; the build it replaces becomes last-known-good if none was marked | the ring is paused; the build does not exceed the ring's high-water mark (superseded) |
| `promote` | beta takes canary's release, or stable takes beta's | the source or target is paused; the release does not exceed the target's high-water mark |
| `pause` | consumers hold what they have | already paused |
| `resume` | forward movement allowed again | not paused |
| `mark-lkg` | the current release becomes the rollback target | it already is |
| `rollback` | the ring serves last-known-good and **authorizes the downgrade**; a pause is kept | no last-known-good; already on it |

The high-water mark is the highest version ever published or promoted into the ring. Rollback
does not lower it, so after rolling back from 1.0.610 to 1.0.608, a 1.0.609 that finished
verifying late cannot re-enter; a fix must be newer than the build rolled away from.

A rollback authorization stays on the ring through pause and resume, and is withdrawn by the
next publish or promotion. Marking last-known-good while the rolled-back release already is the
last-known-good is a no-op and is refused.

Use **Actions → Publish signed internal release → Run workflow** on `main`, choosing the action,
the ring and a reason. Automatic canary publishes share one concurrency group, so a newer pending
build may replace an older pending one; each operator dispatch has a group of its own, so a
pending pause or rollback is never silently replaced. Correctness does not rely on either.

## Authorization

| Operation | Principal | Human gate (required configuration) | Credential | Enforced by consumers |
| --- | --- | --- | --- | --- |
| Publish to canary | `publish.yml` from a successful push-to-main verification run | `v2-canary-release`: main only; no reviewer by design, so verified builds flow | canary environment's `V2_RELEASE_TOKEN` | signature identity and repository/ref claims; monotonic generations; version not below installed unless a signed rollback |
| Promote to beta / stable | operator dispatch of `publish.yml` on main | that ring's environment: main only, reviewers who are not the dispatcher | that environment's token | as above, plus the decision must name this feed and ring |
| Pause, resume, mark last-known-good, roll back canary | operator dispatch on main | `v2-canary-release`: main only; no reviewer, so an incident on canary can be stopped at once | canary token | pause holds consumers; a downgrade needs the signed rollback |
| Pause, resume, mark last-known-good, roll back beta / stable | operator dispatch on main | that ring's reviewers | that environment's token | as above |
| Read the feed and update a relay | `tarkov-group-update.service`, as root on the host | whoever holds root chooses the ring and the floor | read-only token file, mode 0600 | everything in [the relay updater](#the-relay-updater), before the service is touched |
| Offline install or recovery | a local operator, or CI invoking the scripts headless | possession of media is not authorization; the ring decision on it is | none; a trust root file | signature, digest, ring decision, pause, generation floor and downgrade rules; **`-BreakGlass` replaces the ring decision with the operator's own authority** |

None of the human gates exist yet; see
[Controls this repository cannot enforce](#controls-this-repository-cannot-enforce). The matrix
names roles, not people. The repository cannot show who GitHub lets act in them;
[capture that](#capturing-evidence) with each release.

## The relay updater

`deploy/group-server/tarkov-group-update.sh` runs as root from `tarkov-group-update.timer` every
half hour and from the panel's **Update now**. Host configuration lives in
`/etc/tarkov-group/release-feed.env`, loaded by the service unit:

```ini
TARKOV_RELEASE_REPOSITORY=owner/private-feed
TARKOV_RELEASE_RING=stable
TARKOV_RELEASE_TOKEN_FILE=/etc/tarkov-group/release-feed.token
TARKOV_SIGSTORE_TRUST_ROOT=/etc/tarkov-group/sigstore-trusted-root.json
# A floor for a host with no install history: the version (and optionally the generation) the
# publish run's summary reported for this ring when the host was provisioned.
TARKOV_RELEASE_MINIMUM_VERSION=1.0.650
#TARKOV_RELEASE_MINIMUM_GENERATION=12
# Optional: refuse to install a decision signed longer ago than this many days.
#TARKOV_RELEASE_MAX_DECISION_AGE_DAYS=45
# Custom layouts must put every mutable tree, the updater, and its unit directory below one root.
#TARKOV_UPDATE_PATH_ROOT=/srv/tarkov-companion
```

The token file holds a fine-grained token with read-only **Contents** on the feed repository and
nothing else, and must not be readable by anyone but root: the updater refuses a token file with
any group or other permission. The token is passed to `gh` alone; `cosign`, `wget`, Python and
`systemctl` never see it. The host needs `gh`, `jq`, `flock`, Python 3, `wget` and a pinned
`cosign`. Archive extraction is a bounded streaming Python operation; it does not invoke `tar`.

Every signature is checked the same way here as everywhere else, with one rule:
- the bundle must be a standardized v0.3 message-signature bundle over exactly the file's
  bytes;
- the certificate's subject must be `publish.yml@refs/heads/main`, issued by GitHub Actions;
- the certificate's repository must be `smartpbx/tarkov-companion` and its ref `refs/heads/main`.

### Where its state lives, and why not beside the relay

The relay runs as an unprivileged dynamic user and owns `/var/lib/tarkov-group`. The updater
keeps nothing it decides from there. A journal the relay could write would be a journal it could
fill with an updater for root to restore, and a work directory inside a directory it owns is one
it could swap between a signature check and the extraction that follows.

| Directory | Owner | What is in it |
| --- | --- | --- |
| `/var/lib/tarkov-group-update` | root, `0700` | the lock, `work.*` directories, the swap journal, and every `INSTALLED_*`, `PUBLISHED_*` and `REFUSED_*` stamp |
| `/var/lib/tarkov-group-update-status` | root, `0755` | copies of `INSTALLED_SHA256`, `INSTALLED_VERSION`, `PUBLISHED_SHA256`, `PUBLISHED_VERSION` and `REFUSED_SHA256` for the panel, which reads them and cannot write them |
| `/var/lib/tarkov-group` | the relay | `UPDATE_NOW`, which the updater unlinks and otherwise ignores |

The updater refuses to run if its state directory is a symbolic link or belongs to another user,
and tightens it to `0700` if it is root's but looser. The panel refuses a status directory that
is, or is inside, its own state directory. Without `TARKOV_UPDATE_PATH_ROOT`, install, rollback
and updater paths must be strict children of `/opt`; update, status and relay state trees must be
strict children of `/var/lib`; and units must live in `/etc/systemd/system`. A custom root must
itself be below a top-level directory and contain those paths and the unit directory. Every
managed destination and parent is canonical, root/updater-owned and not group/world-writable.
`tarkov-group-update.sh --validate-paths` checks only these containment, ownership and overlap
rules and makes no filesystem changes.

### Every run, before anything on the host changes

1. Takes the lock, unlinks the request marker, and undoes any swap a killed run left behind.
2. Requires the trust root, and the floors and limits above to be well formed. Requires `cosign`
   to be a pinned v3.1.3 build by sha256, owned by root, and not writable by anyone else (see
   [Tools, by content](#tools-by-content)). Online, it also requires a private or internal feed
   that is not the source repository, and the token.
3. Selects the newest `release-index-g*.json` in `rings/RING` and verifies its signature. The
   decision must name this feed and ring, and must carry the generation in its own file name, one
   more than the previous generation it records. That is a monotonic generation number, not a
   hash chain: nothing links a decision to the bytes of the one before it.
4. Refuses:
   - a generation below the configured floor;
   - a generation older than one already installed or authenticated for this ring;
   - a second, different decision at an already authenticated generation;
   - a version below the configured floor, **even with a signed rollback**, because a rollback
     below the floor is as likely to be an old decision replayed. Lowering the floor is how root
     says which it is.
5. Downloads the manifest from `v2-build-VERSION` and verifies its signature, that its digest is
   the one the decision names, and that its version, commit and relay protocol agree with it.
6. Records `PUBLISHED_*`, so what the panel shows as published is only ever something verified.
7. Decides:
   - already running it and healthy: record and stop;
   - no install recorded and nothing to anchor the choice (below): refuse;
   - decision older than the configured age limit: refuse;
   - same version with different bytes: refuse;
   - older than the installed version, or the observed one, without a signed rollback: refuse;
   - ring paused: hold, except for a signed rollback;
   - refused at this or a later generation before: wait for a new decision.
8. Downloads the archive into its private work directory, verifies its signature and digest there,
   and refuses links, special files and unsafe paths.

### Installing

- copies the running tree to `/opt/tarkov-group.lkg` (complete before it replaces the previous
  copy);
- assembles a **swap journal** holding the current units, updater and `INSTALLED_*` stamps, and
  renames it into place, so a journal that exists is complete;
- stops the service, renames the tree aside, renames the new one in, starts the service;
- requires `/health` to report the signed version, commit and protocol;
- installs any changed units and the updater itself from the new build;
- writes `INSTALLED_SHA256`, `_VERSION`, `_COMMIT`, `_RING` and `_GENERATION`, and clears
  `REFUSED_SHA256` and `REFUSED_RELEASE.json`;
- renames the journal to `swap.committed`. That rename is the commit point.

Any failure while the journal exists is undone from it: a stop, a rename, the health check, a unit
install, a stamp rename, the commit rename, or `SIGTERM` from the unit's 20-minute timeout. The
updater restores the previous tree, units, updater and stamps, records the refusal with its ring
and generation, and starts the previous relay. A run killed outright is undone by the next run
from the same journal. A `swap.committed` left behind is simply deleted. So the stamps never name
a build that did not prove itself, and the tick after a refusal reports the refusal rather than
"already on" (`RISK-RELAY-UPDATE-STATE`).

Generations are per ring. Pointing a host at another ring is a root decision and starts that
ring's history; a downgrade still needs that ring's signed rollback.

`TARKOV_RELEASE_BUNDLE_DIR` replaces the network with a directory holding a ring decision, the
manifest, the relay archive and their bundles. Each file is copied into the private work
directory before it is verified, and every check above still applies.

### A host with no history

A host's own stamps are what refuse a replayed older decision. A host without them believes the
newest signed decision the feed shows it, and anyone who can delete newer decisions from the feed
chooses which one that is. The feed token is transport, not authority, and the feed repository's
writers are not the publisher. So a host without history needs an anchor from somewhere else:

- **its own install record**, once it has one;
- **the running relay's `/health` version**, when no install is recorded but a relay answers. This
  covers a host moving from the checksum updater, whose stamps lived where the relay could write
  them and are not read. The relay could lie. A lie can only choose between signed builds at or
  above what it claims, or stop its own updates;
- **`TARKOV_RELEASE_MINIMUM_VERSION` / `_GENERATION`**, provisioned from the publish run's summary
  rather than from the feed;
- or, knowingly, **`TARKOV_RELEASE_ALLOW_UNANCHORED_BOOTSTRAP=1`**, which accepts the newest signed
  decision the feed shows.

With none of these, the updater refuses and says so. A recorded `PUBLISHED_GENERATION` is not an
anchor: it was authenticated the same way. **Freshness is optional.** Decisions are signed only
when a ring changes, so a ring nobody has touched for a month has a month-old decision. With
`TARKOV_RELEASE_MAX_DECISION_AGE_DAYS` set, older decisions are not installed. Without it, a feed
that withholds new decisions holds consumers on an old signed build, undetected by them.

### Moving an existing relay onto the signed feed

The relay on CT 115 follows the public `dev` release today. That publisher has been removed, so
the public release is now a frozen migration source, not a second mutable production channel.
Provision this updater and its units from a verified offline bundle (or from the final already
published legacy archive after verifying its recorded checksum), then configure the private feed
before starting the new update service. The new updater refuses to run without that configuration;
the relay stays on its existing build. Until the new updater has run once, the panel has no status
directory to read and reports no build.

1. Install `gh`, and install `cosign` with `scripts/release/install-cosign.sh /usr/local/bin` (it
   refuses anything but the pinned v3.1.3 binary), so root owns it.
2. On a trusted machine: `cosign trusted-root create --with-default-services --out
   sigstore-trusted-root.json`; record its sha256; copy it to the host.
3. Create the read-only feed token; write it to `/etc/tarkov-group/release-feed.token` (mode 0600).
4. Write `release-feed.env`, including the floor, then `systemctl start tarkov-group-update.service`
   and read the journal. The first run has no install record of its own, takes the running relay's
   version as its floor, and reinstalls the ring's signed build once, restarting the relay. It
   does not adopt a running build it did not install itself.

### Refreshing the trust root

Sigstore rotates the keys behind public-good signing. A host whose trust root predates a
rotation refuses new signatures and stays on its build, which is the safe failure, and logs
that the signature does not verify. Regenerate the file as in step 2, compare, record and deploy
it. Never fetch it from the feed.

## The desktop

The old anonymous `GithubSource` path is removed. An unconfigured `VelopackUpdateGateway` now
fails closed and never asks the public repository for updates. `SignedReleaseFeedConsumer` and
`AuthenticatedGitHubReleaseFeed` provide the typed handoff for #294: a read-only token comes from
the protected integration-secret store; redirects never receive it; the newest bounded decision,
manifest and every selected binary/data/model artifact are verified before one plan is returned.
Pause advances only authenticated generation state, downgrade needs the carried signed rollback,
and an applicable data/model delta must name the installed component digest.

The contracts and transaction coordinator live in `Application`; the authenticated GitHub reader
and cosign process adapter live in `Infrastructure`; only the Velopack presentation gateway remains
in `App`. Every new preparation listing rechecks that the feed is still private or internal before
using the transaction's cached visibility decision for its bounded downloads.

That consumer is deliberately **not composed into the UI yet**. #294 owns activation of all three
components and hands only the verified local `SimpleFileSource` to Velopack; #270 owns the durable
implementation of `IReleaseConsumerStateStore`. Until both are wired, in-app updates report that
the private feed is not configured and signed desktop builds reach machines through the offline
path below. The relay's stamps remain host files in root's own directories, not application data.

The installer still installs per user under `%LOCALAPPDATA%\TarkovCompanionDesktop`, separate
from data under `%LOCALAPPDATA%\TarkovCompanion`; see [WINDOWS.md](WINDOWS.md#release-and-installation).

## Offline installation and recovery

An offline bundle is the files of one `v2-build-VERSION` release and the ring decision that
selected it:

```bash
gh release download v2-build-1.0.608 --repo owner/private-feed --dir /media/tarkov-1.0.608
gh api -H "Accept: application/vnd.github.raw+json" \
  repos/owner/private-feed/contents/rings/stable/release-index-g0000000012.json \
  > /media/tarkov-1.0.608/release-index-g0000000012.json
scripts/release/verify-offline.sh --ring stable --feed owner/private-feed --minimum-generation 12 \
  --output /srv/verified-1.0.608 /media/tarkov-1.0.608 /path/to/sigstore-trusted-root.json
```

On Windows, headless:

```powershell
pwsh -File scripts/release/install-offline.ps1 `
  -BundleDirectory E:\tarkov-1.0.608 `
  -TrustedRoot C:\ProgramData\TarkovCompanion\sigstore-trusted-root.json `
  -Ring stable -FeedRepository owner/private-feed -MinimumGeneration 12 `
  -Headless
```

Both copy what they use off the media into a private directory first and verify it there. The
installer runs its private copy of the installer, never the file on the media. So media that
changes between the check and the use changes nothing that was checked. `verify-offline.sh
--output` keeps its verified copy for whatever installs from it.

Both require, before anything is used:
- a pinned cosign;
- a standardized bundle for, and a valid signature on, the manifest and every file;
- a signed ring decision for the named ring and feed that selects exactly this manifest;
- that decision not paused, unless it is a signed rollback;
- that decision at or above the minimum generation.

The installer also refuses a version older than the installed one unless the decision is a signed
rollback or `-AllowDowngrade` is given. It treats an installation whose version cannot be read as
possibly newer, so a missing or unreadable `BUILD_INFO.txt` also needs `-AllowDowngrade`.
`-WhatIf` performs every check without installing.

What offline does not do:
- It keeps no history of its own. A desktop does not remember generations across installs, so
  replay protection there is `-MinimumGeneration`, which the operator supplies.
- `--break-glass` / `-BreakGlass` verify the manifest and every file without any ring decision,
  and say so loudly. That is the operator's authority replacing the ring's, for recovery when no
  decision is available, and ring, pause, rollback and generation policy are not applied.

For a relay, set `TARKOV_RELEASE_BUNDLE_DIR` as described above; the updater's own history and
floors still apply.

## Tools, by content

Every tool that makes or checks release bytes is fetched by version and accepted only by digest,
from a file committed here. Nothing is installed by a tag, a floating action, or a remote install
script.

| Tool | Where it runs | Pinned by | Evidence for the pin |
| --- | --- | --- | --- |
| cosign v3.1.3 | release job, License lock, relay hosts, offline scripts | `scripts/release/cosign.sha256`; embedded in the relay updater and the offline installer, held equal by `test_cosign_pins.py` | cosign's own `cosign_checksums.txt`, whose Sigstore bundle verified for `keyless@projectsigstore.iam.gserviceaccount.com`; equal to GitHub's asset digests |
| Syft 1.51.1 | `sbom` job | `scripts/release/syft.sha256` | Syft's `syft_1.51.1_checksums.txt`; equal to GitHub's asset digests |
| actionlint 1.7.12 | License lock | digest in `license-lock.yml` | the release tarball, checked |
| PyYAML 6.0.3 | License lock (workflow policy) | `scripts/release/policy-requirements.txt`, wheels only, `--require-hashes` | PyPI's digests, checked against the downloaded cp312 x86_64 wheel |
| vpk 1.2.0 | Windows verification | `scripts/release/vpk.sha256`; restored from a bounded NuGet package with a local-only NuGet configuration | the package downloaded from NuGet and checked against the committed digest |
| GitHub Actions in every workflow | all | full commit SHAs; `check_workflow_policy.py --enforce-all --verify-tags` resolves each commented tag to its commit in CI | the resolution itself |

Why cosign v3.1.3 and nothing earlier: before it, `cosign verify-blob` given a **legacy** bundle
whose `cert` field held a bare public key skipped certificate chain and identity checks.
`--certificate-identity` then pinned nothing ([GHSA-fx35-mq7g-6g98](https://github.com/sigstore/cosign/security/advisories/GHSA-fx35-mq7g-6g98)).
Beyond the patched version, every consumer refuses any bundle but a standardized v0.3
message-signature bundle before cosign runs:
- exactly one certificate and one transparency-log entry;
- a SHA2_256 message digest equal to the file's own;
- no legacy fields, DSSE envelope, bare key or certificate chain.

`scripts/release/test-real-sigstore.sh` shows the pinned cosign, given such a legacy bundle,
refusing it on its own. With a trust root it demands the standardized format. Without one it no
longer falls back from an unparsable certificate to a key.

## Cancellation and failure

| Stage | Interrupted or failing | Left behind |
| --- | --- | --- |
| `request`, `candidate`, `sbom`, `relay-smoke` | job fails | nothing: no job before `release` holds a write credential |
| Artifact fetch | a digest mismatch, an expired or ambiguous artifact | nothing; the run fails before anything is signed |
| Signing and attestation | job fails | attestations for artifacts that were never published; harmless |
| Feed preflight | feed public, immutable releases off or unreadable | nothing written |
| Build upload | draft with some assets | an unreferenced draft, replaced by the next attempt |
| Build publication | published build, no decision | adopted by a re-run if it is immutable and the same build |
| Decision write | conflict or error | the previous decision, whole; a conflict is retried |
| Pending operator dispatch | cancelled in the Actions UI | nothing changed; dispatch again |
| Desktop feed preparation | cancellation, malformed input, signature/digest mismatch or explicit refusal | its private staging directory is removed after verifier exit and pipe drainage are confirmed; if bounded verifier cleanup cannot prove quiescence, that directory is quarantined for operator cleanup; installed/LKG state is unchanged until the caller atomically activates all components and commits |
| Relay updater | any step | pre-swap: nothing changed; after the journal exists: previous build, units, updater and stamps restored |
| Offline scripts | any check | nothing installed; the private copy removed |

Re-running a failed publish is always safe: every write is either create-once or checked against
what already exists.

## Controls this repository cannot enforce

`scripts/release/capture_controls.py` reads these from GitHub and records who captured them and
when. It only reads. The state on **2026-09-15T04:08:06Z**, captured by `smartpbx`:

| Control | Required | Observed |
| --- | --- | --- |
| main: force pushes and deletion blocked | neither allowed | met |
| main: verification checks required | `checks`, `windows-verify` among required checks | met (`checks`, `linux`, `windows-build`, `windows-verify`) |
| main: supply-chain gate required | `supply-chain` (License lock) among required checks | **gap**: not required. A pull request that fails the dependency review, the workflow policy or the release fixtures can still be merged; publication's own release-time gates in `candidate` still apply |
| main: reviewed before merge | at least one approving review | **gap**: no review requirement |
| main: administrators cannot bypass | enforced for administrators | **gap** |
| Default `GITHUB_TOKEN` permission | read | **gap**: write |
| Actions pinned to commit SHAs | required at repository level | **gap**: not required as a repository rule. Every checked-in workflow action is nevertheless pinned to a full commit and the workflow policy rejects future mutable action tags |
| Secret scanning and push protection | enabled | **gap**: disabled |
| Dependency graph | enabled, so the pull-request dependency review can run | met |
| Dependabot vulnerability alerts | enabled | met |
| `v2-canary-release`, `v2-beta-release`, `v2-stable-release` | exist; main only; beta and stable with reviewers who are not the dispatcher; `V2_RELEASE_TOKEN` and `V2_RELEASE_REPOSITORY` | **gap**: none exist |
| Private feed repository | private or internal, immutable releases on, token with Contents write and Administration read | **gap**: none configured |

Releases are **off** until the repository variable `V2_RELEASES_ENABLED` is `true`: verified
builds skip `publish.yml` and an operator dispatch fails saying why. Turn it on only after the
environments and feed exist, because a job that names an environment GitHub does not know about
creates it on the spot, with no protection. Each gap above is a setting for the repository owner,
not something to paper over in YAML.

### Capturing evidence

```bash
scripts/release/capture_controls.py --feed-repository owner/private-feed \
  --output release-evidence/controls-$(date -u +%Y%m%dT%H%M%SZ).json
```

Run it with a token that can read repository administration settings; anything it cannot read is
recorded as unreadable, not as met. Keep the output with the release record together with the
publish run URL, the approving reviewer shown on that run, and the signed ring generation. Add
`--require` to make it fail while any control is unmet.

## Verification of this machinery

| What | Where |
| --- | --- |
| Ring policy, envelope, generation selection | `scripts/release/tests/test_release_policy.py` |
| Manifest reconciliation and refusals, including files the upload did not contain | `test_build_manifest.py` |
| Artifact fetch by recorded digest; provenance predicate | `test_artifacts.py` |
| Feed writes: create-once decisions, races, drafts, digest checks, immutability, adoption | `test_feed.py`, `test_transition.py` (end to end through the real scripts with a fake GitHub and a digest-bound fake cosign) |
| Release gates | `test_gates.py` |
| Workflow policy, including YAML forms the line matcher missed; all-workflow coverage; pins resolved against GitHub in CI | `test_workflow_policy.py`, `check_workflow_policy.py --enforce-all --verify-tags` |
| Cosign pins equal everywhere; unpinned cosign refused by every script | `test_cosign_pins.py` |
| Relay updater: root-owned state and a hostile relay directory; every post-swap failure, SIGTERM and interrupted commit; hostile bundles; pinned cosign; replay, floors, bootstrap, freshness, downgrade, pause, rollback, locale; refusal truthfulness; token scope | `test_relay_updater.py` |
| Offline verification and the PowerShell installer: private copies, ring decisions, break-glass, hostile bundles, unreadable installed versions | `test_offline.py`; `test-offline-windows.ps1` runs the real installer on Windows with successful, no-op and wrong-identity installers, and refuses inconsistent rollback authority |
| Desktop private-feed transport, filesystem staging and one binary/data/model verification transaction; live private-visibility checks, cancellation/process cleanup, real Sigstore verification, pause, replay, downgrade, rollback and component-delta selection | `AuthenticatedGitHubReleaseFeedTests.cs`, `CosignReleaseSignatureVerifierTests.cs`, `ReleaseStagingStoreTests.cs`, `SignedReleaseFeedConsumerTests.cs`, and `test-real-sigstore.sh` |
| Control capture | `test_capture_controls.py` |
| The verification command against real Sigstore material, and a real GHSA-fx35-mq7g-6g98-shaped legacy bundle | `scripts/release/test-real-sigstore.sh` |
| Panel reports only the root-owned status | `tests/TarkovCompanion.UnitTests/RelayUpdateTests.cs` |

The **License lock** workflow runs all of it, with actionlint, on any pull request touching the
paths it watches. `publish.yml` itself cannot run before it is on main, and has not run against
a real feed: the first real publication, and the first signed decision a relay installs, are
still to be observed and recorded.

## Residual risks

- **Freeze.** Whoever controls the feed can stop new decisions reaching consumers. Decisions carry
  a timestamp; a relay refuses decisions older than `TARKOV_RELEASE_MAX_DECISION_AGE_DAYS` only if
  that is set, and rings are not re-signed on a schedule. A withheld update is otherwise
  detectable by comparing with the Actions history, not refused.
- **A host with no history and no floor** accepts the newest signed decision the feed shows. The
  updater refuses to choose without an anchor unless told otherwise, but an anchor taken from the
  running relay's `/health` is only as honest as that relay.
- **Publisher compromise.** Anyone who can change `publish.yml` on main, or approve its
  environments, can sign. Branch protection and environment reviewers are the control, and both
  are gaps today.
- **Provenance is attested by the publisher.** It is checked against GitHub's records of the
  verification run, but it is not signed by the workflow that built.
- **Repository policy does not itself require SHA pins.** The checked-in workflow policy enforces
  them across every workflow, but an owner must still configure GitHub's independent SHA-pinning
  rule before enablement.
- **Break-glass offline installs** apply no ring policy, by design.
- **Desktop in-app updates** are fail-closed until #294 composes the authenticated consumer and
  #270 supplies its durable state implementation; they no longer fall back to the public feed.
- **The update service itself** runs as root with no systemd sandboxing beyond its own checks;
  narrowing it with `ProtectSystem=`/`ReadWritePaths=` is untested on CT 115 and not done here.
