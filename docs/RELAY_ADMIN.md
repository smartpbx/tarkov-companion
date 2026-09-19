# Relay administration

What an operator can see and do on the group relay, what the relay keeps on disk on their behalf,
and the limits it enforces on the reports players send. The deployment itself is in
[`deploy/group-server/README.md`](../deploy/group-server/README.md); the runbooks are in
[`docs/runbooks/`](runbooks/); what is public and what is not is in [`PRIVACY.md`](PRIVACY.md).

## Who is the operator

One person, holding one secret. `TARKOV_RELAY_ADMIN_KEY` is set on the unit; a request that carries
it in `X-Admin-Key` is the operator's. It is compared in fixed time, and a relay with no key
configured refuses every operator request rather than allowing them. It is separate from the group
key on purpose: every member of every group holds a group key, and none of them may read reports or
decide which rooms exist.

That is the whole identity model today. There is no per-operator identity, no operator session, no
cookie and no CSRF token on `/admin`: the key is the credential and the page keeps it in
`sessionStorage` for the tab. Splitting that into named operators with revocable sessions is not
built (#310) and nothing below pretends otherwise. `RelaySessionCookie` and the CSRF protector exist
and are unused for exactly that reason.

## Operator routes

All require `X-Admin-Key`. None is cached.

| Route | What it does |
| --- | --- |
| `GET /admin` | The operator page (rooms, build, update). |
| `GET /admin/readiness` | Whether the relay is fit to serve: storage, disk, clock, build, updater, latency, failures, rate-limit and rejected-input pressure, plus room/member/held counts. |
| `GET /admin/rooms`, `POST /admin/rooms`, `DELETE /admin/rooms/{room}` | The room allowlist. Once one room is registered, only registered rooms are served. |
| `GET /admin/update`, `POST /admin/update` | Installed build against the signed release ring's; asks the updater to run now. |
| `GET /admin/reports` | Every held report's reference, size, arrival time and state, the quota and disk state, and the limits in force. Never a body. |
| `GET /reports` | The filing queue: reports still to be turned into issues. Reference, size and arrival time only; the schema is closed because the workflow refuses anything else. |
| `GET /reports/{reference}` | One report's body. |
| `POST /admin/reports/{reference}/processed` | The issue exists. Idempotent. |
| `POST /admin/reports/{reference}/failed` | Filing failed; counts an attempt. |
| `DELETE /admin/reports/{reference}` | Removes the report. Idempotent. |

## Problem reports

A player presses **Report a problem**; the desktop sends the closed support projection to
`POST /report` under their group key. The relay keeps the text and files no issue: it holds no
GitHub credential. The hourly relay-watch workflow lists references and opens an issue naming each
one, and the issue never contains the body.

### Lifecycle

| State | Meaning | How a report gets there |
| --- | --- | --- |
| received | Kept, not yet turned into an issue. | `POST /report` succeeded. |
| processed | An issue names it. Stays until it expires or is deleted. | `POST /admin/reports/{reference}/processed`. |
| failed | Filing was attempted and did not work. Retried until the attempt limit. | `POST /admin/reports/{reference}/failed`. |
| deleted | Gone: body and state. | `DELETE`, or expiry. |

Every transition is idempotent: repeating a request leaves the report where the first one did.
`processed` is never downgraded to `failed`. A report that has failed the attempt limit (five)
drops out of `GET /reports` so a permanently unfileable one cannot be retried for ever; it remains
visible at `GET /admin/reports` until it expires.

Duplicate issues are prevented on both sides: the workflow looks for an existing issue titled with
the reference before opening one, and the relay stops listing a report once it is processed.

### Limits

| Limit | Default | Setting | On exceeding it |
| --- | --- | --- | --- |
| Body size | 64 KiB | fixed | `400`, from the endpoint; Kestrel's own limit is raised for this route only. |
| Per room per hour | 3 | fixed | `400`, saying the earlier ones arrived. |
| Per room held | 10 | fixed | `503` until one is deleted or expires. |
| Whole relay per hour | 60 | fixed | `503`. |
| Reports held | 200 | `TARKOV_RELAY_REPORT_MAX_HELD` | `503`. |
| Bytes held | 16 MiB | `TARKOV_RELAY_REPORT_MAX_MEGABYTES` | `503`. |
| Free disk | 256 MiB plus the report | `TARKOV_RELAY_REPORT_MIN_FREE_MB` | `503`: a full disk stops reports, not the relay. |
| Time to live | 30 days | `TARKOV_RELAY_REPORT_TTL_DAYS` | Deleted by an hourly sweep. |

A refusal is a plain-text `503` the desktop shows as "The relay refused it (503)…" beside Copy
diagnostics, so the player is told the report did not arrive and still has the copy.

### On disk

`reports/<yyyyMMdd-HHmmss>-<reference>.md` is the body, written to a temporary file and renamed so
a crash never leaves half a report where the listing would count it. `<same>.state` beside it holds
the state, the attempt count and the room's hash (which is what the per-room cap counts). A state
file that cannot be read means `received`: the report is not lost and worst case is a second
look, which the workflow's duplicate check absorbs. Temporary files and state files with no body
are swept after an hour. Reports are not in the updater's tree, so an update does not remove them.

### At rest: an accepted risk, and why

Report bodies are stored as plain text under the relay's state directory. That is a decision, not an
omission. Application-level encryption would need a key the same process can read, on the same box,
so anyone who can read the files could read the key: it would protect nothing that host permissions
do not, and it would stop the operator reading a report to answer it.

What protects them instead, in order of how much they matter:

1. **Minimisation.** The ordinary desktop sends a closed projection of counts, categories and build
   facts; no path, name, coordinate, screenshot or log line. The endpoint still accepts any text up
   to 64 KiB, so this rests on the client, not on the relay.
2. **Retention.** 30 days, then deleted; `DELETE` sooner.
3. **Host confinement.** The unit runs as a dynamic user with `ProtectSystem=strict`; the state
   directory is that user's and root's. The bodies are never attached to a public issue.
4. **One bearer secret** reads them, which is the weakest control here. Anyone holding
   `TARKOV_RELAY_ADMIN_KEY` can read and delete every report and change the room list.

Treat the backup of the state directory as sensitive. Revisit this if the relay ever accepts a body
from anything but the desktop's closed projection, or if operators other than one person need access.

## What is not built

- Per-operator identity, sessions, CSRF and audit of operator actions.
- The workflow calling `POST /admin/reports/{reference}/processed` after it files an issue. Today
  duplicate prevention in the workflow is the title check alone; the relay-side state is reachable
  by an operator or a later workflow change.
- Structural (schema) validation of a report body on the relay. It bounds and rate-limits; it does
  not parse.
- Preview and explicit consent on the desktop before a report is sent.
