# ADR 0011: Signed release rings in a private feed

Status: Accepted — 2026-09-15 (issue #280)

## Context

Until v2, every verified build on main replaced the assets of one public, rolling `dev` release,
and both consumers followed it: the desktop through Velopack's GitHub source, the relay through
an updater that compared a checksum downloaded from that same release. The checksum could detect
a corrupt download and nothing else. Anyone able to change the release could change the archive
and its checksum together, including back to an older build, and the relay would install it
(`RISK-UPDATE-CHANNEL-TRUST`). The feed was anonymous and mutable, `--clobber` deleted an asset
before replacing it, and there was no way to stage a build before everyone received it, to stop a
bad one, or to go back.

The same workflow built, verified and published, so the job holding the release token also ran
the code it had just built. The relay updater also wrote its installed stamp before its health
check, so a refused build was later reported as installed (`RISK-RELAY-UPDATE-STATE`).

## Decision

**Authority is a signature.** Every artifact, the release manifest naming them, and every ring
decision is signed keylessly with Sigstore by `.github/workflows/publish.yml` running on
`refs/heads/main` in `smartpbx/tarkov-companion`. Consumers accept exactly that certificate
identity and issuer, and require the certificate's GitHub repository and ref claims to match. They
verify against a Sigstore trust root they hold as a file, provisioned separately from the feed
and never fetched from it. A workflow dispatched from any other branch gets a certificate naming
that branch, and consumers refuse it.

**Verification is by content, in one format.** Signing and verification use cosign v3.1.3,
accepted only by sha256; earlier versions skipped the identity check for a legacy bundle carrying
a bare public key (GHSA-fx35-mq7g-6g98). Every consumer accepts only a standardized v0.3 Sigstore
message-signature bundle over exactly the file's bytes, and refuses every other bundle shape
before cosign runs. Syft, actionlint and the workflow policy's YAML parser are likewise installed
by digest, and actions by commit.

**The feed is a private GitHub repository**, named per release environment, and must be private
or internal; the publisher and the relay updater both refuse the public source repository.

- An immutable build is a release tagged `v2-build-<version>`. It is uploaded as a draft, and
  GitHub's own sha256 for each asset must match the signed manifest before and after the draft is
  published. The publisher requires the feed to enforce immutable releases, read live before it
  writes, and requires GitHub to report each published build immutable.
- A ring (canary, beta, stable) is a sequence of signed decisions with monotonic generation numbers,
  `rings/<ring>/release-index-g<generation>.json`, each created once through the contents API
  without a parent blob. A second writer for the same generation is refused by GitHub rather than
  overwriting the first. Readers take the newest generation.

**Verification and publication are separate workflows**, and publication is split by
authority. Each job's authority:
- a job that checks holds read scopes;
- the job that generates the SBOM runs no .NET;
- a job that runs the built relay holds nothing;
- only the job in the ring's protected environment can sign, attest and write the feed.

Every job fetches the verification run's artifact archives by the sha256 GitHub recorded at
upload, and the manifest names those digests. Provenance is an explicit predicate. It names the
verification workflow as builder, its run as invocation, the commit it built as source, and the
archives as inputs. It does not describe the publishing run, which built nothing.

**Ring policy** is code (`scripts/release/release_policy.py`):

- Only verified builds enter canary; beta and stable take only the ring below's current release.
- Each ring has a high-water mark that forward moves must exceed, and a rollback does not lower.
- Pause holds consumers. Rollback selects the marked last-known-good and is the only decision
  that authorizes a consumer to install an older version.
- Every decision records its predecessor's generation number, the action, actor, reason and
  workflow run. It carries no digest of its predecessor; generations are monotonic, not chained.

**Consumers enforce, not just publishers.** The relay updater:
- verifies everything before touching the host;
- refuses older or conflicting generations and unauthorized downgrades;
- changes its stamps only after the new relay proves the signed identity;
- undoes any failure from a swap journal.

Everything it decides from is in a root-owned `0700` directory. The unprivileged relay can write
only the request marker, and the panel reads a root-owned status directory. A host with no history
needs an anchor: its running relay's version, a provisioned version or generation floor, or an
explicit unanchored bootstrap. Offline media are copied privately before verification. They must
carry a signed ring decision for the named ring and feed, unless an operator breaks glass, which
is reported as exactly that.

The desktop has the same bounded authenticated reader and signature rule. It stages binary, data
and model artifacts as one verified plan, applies pause and rollback from the signed decision,
and records state only after a caller activates the plan. The existing UI gateway is fail-closed;
#294 owns composition and activation, and #270 owns the state-store implementation.

## Alternatives considered

- **Keep the public `dev` release and add signatures.** Signatures would stop forgery but leave
  an anonymous feed, deletion-before-upload, and no rings, pause or rollback. The v2 release
  requirement is also for authenticated feeds.
- **Self-host the feed on the relay host.** Update availability would depend on the host being
  updated, storage would sit behind the group's tunnel, and there would be a new service to
  operate. Offline bundles already cover the case where GitHub is unreachable.
- **Object storage or a CDN.** This needs a credential model, an access model and an audit trail
  built from scratch for a group of a handful of people. GitHub already provides fine-grained
  read-only tokens, commit history for every decision, and immutable releases.
- **Key-based signing with a stored private key.** A long-lived key must be stored, rotated and
  protected, and its theft is silent. Keyless signing binds each signature to a short-lived
  certificate for the reviewed workflow and records it in a public transparency log.
- **Detached index plus signature files, or release assets for ring state.** Two files cannot
  be replaced atomically, and a clobbered asset is absent while it uploads. One envelope in a
  create-once path is.

## Consequences

- **Trust.** Feed compromise cannot make a consumer accept bytes the publisher did not sign. It can
  withhold releases, and it can delete newer decisions so that an older signed one is the newest a
  consumer sees. A consumer's own history refuses that; a host with no history is protected only
  by a provisioned floor. A break-glass offline install applies no ring policy at all.
  Compromise of `publish.yml` on main, or of an environment approver, can sign anything, so
  protection of main and of the environments is release-critical. When this ADR was accepted
  neither was configured, as `docs/RELEASES.md` records.
- **Availability and freeze.** GitHub and public-good Sigstore are dependencies. Decisions carry
  no expiry. A relay can be told to refuse decisions older than a number of days, but rings are not
  re-signed on a schedule, so a withheld update is otherwise not refused automatically. A consumer whose trust root
  predates a Sigstore key rotation refuses new signatures and stays on its build until the root
  is refreshed out of band.
- **Rollback.** Downgrade becomes an explicit, signed, audited decision instead of something any
  feed writer could cause. A rollback keeps a pause, and a late build below the high-water mark
  cannot re-enter. Rolling back the application does not roll back a newer database schema; that
  refusal belongs to #270 (`RISK-PERSISTENCE-SCHEMA-COMPATIBILITY`).
- **Operations.**
  - Releases stay off until the environments, the feed and `V2_RELEASES_ENABLED` exist.
  - Each relay needs `gh`, the pinned `cosign`, a read-only token, a trust root, and (for a new
    host) a floor before its updater will run. An existing relay stays on its build until it has
    them.
  - The feed token needs Administration read on the feed, so the publisher can confirm immutable
    releases.
  - The desktop's in-app updater remains disabled until #294 composes the authenticated consumer.
    Until then signed desktop builds are installed from verified offline bundles; there is no
    fallback to the public feed.
  - The legacy `dev` publisher is removed. Migrating an existing relay is now an explicit
    verified offline/provisioning step rather than another release writer remaining active.
- **Visibility.** Public-good Sigstore publishes artifact digests, names and the source workflow
  identity. For this public source repository that is already public information.
