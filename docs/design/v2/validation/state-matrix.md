# State matrix

> **Status: not yet run.** This is a specification for sessions and for implementation issues to
> test against. No state here has been validated with participants.

Every workspace has eight states. For each, this matrix says what **triggers** it, what the player
can **still use**, what **provenance** is shown, how they **recover**, what happens to **focus and
announcements**, and how **desktop and paired tablet** stay consistent. The storyboards render every
cell (moderator panel, **Workspace state**) using wording abbreviated from here; this file is
authoritative.

Shared contracts this depends on are owned elsewhere: the capture session lifecycle by #264 and
#271, the paired-device protocol by #276, readiness and diagnostics by #281, and the shell's state
controls by #266 and #267. Where this matrix needs something those contracts do not yet define, it
says "needs contract" rather than inventing it.

## Rules that apply to every cell

1. **No dead ends.** Every non-success state names what still works and offers at least one recovery
   action. "Something went wrong" alone is never a state.
2. **States are distinct.** Empty (nothing exists yet), loading (it is coming), offline (the network
   is unavailable), stale (the data is too old to trust fully), partial (some of it is missing),
   permission denied (the system or a role refuses), and failed (an operation errored) each look and
   read differently. None is shown as another.
3. **Unknown is not zero.** Missing price, weight, count, coverage or outcome is shown as unknown and
   excluded from totals, with the count of what is known ("29 of 31 priced").
4. **Evidence travels with the fact, with progressive disclosure.** A compact source/context label
   stays next to what it qualifies. Freshness or confidence is inline when it can change the user's
   choice. Adjacent **Why** or details exposes source, observed/data-through/generated times,
   coverage, confidence meaning and model version without navigating away. Modelled layers are
   labelled modelled, never live. The disclosure changes density, not the evidence contract (C-02,
   C-07); full Safety and data methodology are linked from Setup & Admin/Data and Privacy.
5. **Focus moves only for the player's own action.** A background change (a sync arriving, data
   going stale, the relay dropping) never moves focus. A failure of an action the player just took
   moves focus to that failure's message or recovery control. A refusal with no alternative keeps
   focus on the control, with the reason attached to it; a refusal that offers choices opens a
   dialog with focus on its heading.
6. **The remainder must replace contradicted success content.** A degraded banner alone is not
   enough when the normal body still says a check is Found/Available/Synced, a correction saved, or
   nothing needs action. Keep genuinely usable controls and historical facts, but mark unchecked
   readiness as unconfirmed and a failed correction as a draft until recovery succeeds.
7. **Recovery is not fabrication.** A recovery labelled as navigation, explanation, chooser or
   cancellation keeps the current degraded state until its dependency is actually resolved; only a
   completed retry or refresh may replace it with Success. The storyboard's moderator scenarios
   transition the whole canonical scenario together (for example, post-raid chrome and Raid state),
   never a single decorative flag.
8. **Announcements are proportionate.** Background changes use the polite status region, once per
   transition, never repeatedly while a state persists. Only a failure or refusal of the player's own
   action, or a conflict that discarded their change, uses the assertive region. Loading is announced
   only if it lasts longer than about one second, and completion is announced once.
9. **Desktop is canonical.** A paired tablet shows the desktop's state with the desktop's provenance.
   A problem on the tablet's own link is labelled as the tablet's ("This tablet can't reach your
   desktop"), never as the desktop's data being offline. Ages are computed from observation time, not
   from when the tablet received the update.
10. **Success still carries evidence.** The success row is not "no banner, no metadata"; compact
   context remains visible and the complete evidence remains available in adjacent details.

---

## Raid

| State | Trigger | Usable remainder | Provenance shown | Recovery | Focus and announcement | Desktop and tablet sync |
| --- | --- | --- | --- | --- | --- | --- |
| **Empty** | No raid active in the game log (with or without a chosen map) | Choose a map; open the current plan; model layers once a map is chosen; Capture; manual raid-state entry | "No raid seen in the game log since 18:02"; which log folder is watched | Choose map · Open plan · Check log folder (Setup) | No focus move. Polite "Raid: no raid active" only when a raid ends and the page becomes empty | Tablet shows the same empty state; in Control mode the tablet can choose the desktop's map |
| **Loading** | Map tiles, floor data or a model snapshot loading after a map change | Extracts, objectives and routes as a list immediately; map region shows text progress; unrelated layers stay usable | Which layer is loading and from where ("Customs tiles, tarkov.dev, 120 of 170") | Use list view · Cancel · after a delay "Still loading: Retry" (**needs contract**: #266) | Focus stays where it was. Polite "Loading Customs map" after 1 s; "Customs map ready" once | Tablet loads its own tiles and labels its progress as the tablet's; desktop's loading does not block tablet list view |
| **Offline** | Tile host, catalog or model source unreachable | Cached tiles, cached catalog, last-known-good model snapshot; screenshot position and log raid state (both local) | "Offline since 18:31"; cache dates; model version and data-through date of the snapshot in use | Automatic retry with the next attempt time · Retry now | No focus move. Polite "Offline. Using cached data from 2026-09-13" once | Local-network pairing continues if the LAN is up. If the tablet reaches the desktop only through the relay, the tablet shows "This tablet can't reach your desktop; last update rev 42 at 18:40" |
| **Stale** | Position older than its threshold (no new screenshot), raid log not updated, or model data older than a game, map or wipe change | Everything, each labelled with its age; old position drawn hollow with its age | "Position from 18:36 (6 min old)"; "Model predates the 2026-09-10 map update" | Say how to refresh ("Take a screenshot to update position"); a model incompatible with the current map is qualified or switched off, not silently used | No focus move. Polite once when a threshold is crossed, not on every tick | Age shown identically on both, from observation time |
| **Partial** | Some layers or facts missing: extract list not photographed, floors unknown, model covers only some zones, some tiles blank; older screenshots are online-only in OneDrive and are not downloaded automatically | Available layers; missing ones named with the reason; routes degrade to waypoints where geometry is unknown | "Traffic covers 9 of 12 zones"; "Extracts: catalog possible, not confirmed this raid" | Photograph extract list · Choose extract manually · Show uncovered zones | No focus move; no announcement beyond visible labels | Same coverage on both; tablet never fills gaps the desktop does not have |
| **Permission denied** | Operating system refuses the log or screenshot folder (access control); **or** a paired device without control permission tries to change the desktop | Manual map, extract and raid-state entry; plan; model layers; the tablet can still browse Independently | Which folder, since when; which role or mode refused | Choose folder (Setup) · Ask the desktop to allow control (desktop approves) | For the player's own blocked action: focus stays on the control, message tied to it, assertive announcement. For a background denial: polite once | Denial recorded on the desktop; the tablet shows why its action was refused; a control request appears on the desktop for approval |
| **Failed** | Map rendering or model loading errors; log parse error | List view with extracts, objectives and routes; manual raid-state correction | Time, component, a correlation reference in an expandable diagnostic | Retry map · Use last-known-good model · Report a problem (with preview) | If the player's map change caused it: focus to the failure heading, assertive. Otherwise polite | Tablet shows the desktop's failure with the same reference; a tablet-only render failure is labelled as the tablet's |
| **Success** | Raid observed in the log, layers loaded | All | Compact source labels; material age or confidence inline; complete model evidence in adjacent Why/details | Correct raid state is always available | Polite "Raid started: Customs, PMC (game log)" when a raid starts; no focus move | Every change carries a revision (**needs contract**: #276); the tablet shows "Desktop showing Raid · Customs, rev N" |

## Intel

| State | Trigger | Usable remainder | Provenance shown | Recovery | Focus and announcement | Desktop and tablet sync |
| --- | --- | --- | --- | --- | --- | --- |
| **Empty** | No search yet, no item selected | Recent, pinned and planned items; type filters; browse by category | Where each suggestion comes from ("in your current plan") | Search | No announcement | Tablet search is independent unless in Control or Follow mode, where the desktop's query and selection are mirrored |
| **Loading** | Search index building or prices refreshing | Cached facts and prices shown immediately with their age; search over what is indexed so far | "Prices refreshing; showing prices from 18:30" | Cancel refresh | No focus move. Polite "Refreshing prices" after 1 s, then "Prices updated" once | Tablet shows the desktop's refresh state; its own cached prices keep their ages |
| **Offline** | Price or catalog source unreachable | Cached catalog, cached prices, local price history | Price age on every value; "Offline since …" | Retry now · automatic retry time | Polite once | Same on both; LAN pairing unaffected |
| **Stale** | Prices older than their threshold; catalog older than a game patch | Everything, with stale prices labelled and excluded from SWAP and sell advice that depends on them | "Price 3 h old (stale)"; which advice ignores it | Refresh prices | Polite once when crossing the threshold | Same labels on both |
| **Partial** | Some facts unknown (weight, a trader, a barter source) | Known facts; totals computed only from known values with the known count | "9 of 10 facts known"; the missing fact named; never shown as zero | Show what is missing | No announcement | Same on both |
| **Permission denied** | The data folder cannot be written (history not recordable) | Current facts and cached history | Which folder and what is not being recorded | Open Data settings | Polite once; assertive only if the player's own action (pin, override) was refused | Tablet sees the desktop's denial; pin attempts from the tablet are refused with the reason |
| **Failed** | Search index error; item detail lookup error | Browse by category; recent items | Time and diagnostic reference | Rebuild search · Retry | Player's own search: focus to the failure message, assertive; background: polite | Same reference on both |
| **Success** | Search and details available | All | Every price with source, gross, estimated fee, net, age; every need with its source; owned counts with scan time and coverage | — | "N results" polite after a search; opening details moves focus to the details heading | Selected item and search are part of shared state in Follow and Control modes; Intel addresses can be sent to the tablet |

## Plan

| State | Trigger | Usable remainder | Provenance shown | Recovery | Focus and announcement | Desktop and tablet sync |
| --- | --- | --- | --- | --- | --- | --- |
| **Empty** | No bundle created or opened | Suggested bundles generated from local quests; quest, hideout and loadout browsing | Why each bundle is suggested; profile it came from | Open a suggested bundle | No announcement | Same suggestions on both |
| **Loading** | Route estimate or requirement check calculating | Objectives and requirements immediately; route region shows progress | "Calculating route with traffic-sample-v0" | Skip route estimate | Polite after 1 s; completion once | Tablet shows desktop's calculation; a tablet in Independent mode may calculate its own and labels it |
| **Offline** | Route model or price source unreachable | Quests, hideout, loadout, last-known-good route model, cached prices | Model version and data-through date in use; price ages | Retry now | Polite once | LAN pairing unaffected; plan edits queue locally and say so (**needs contract**: #276, which commands may queue offline) |
| **Stale** | Profile progress, owned counts or model older than their thresholds | Plan, with stale inputs labelled | "Progress imported 3 days ago"; "Key seen in a scan at 18:20, 42 of 68 rows" | Update progress · Rescan | Polite once | Same on both |
| **Partial** | A requirement not verifiable; route geometry incomplete | Confirmed requirements; unknown ones listed as unknown; waypoints instead of a precise line | "2 confirmed · 1 unknown"; which geometry is missing | Check requirement · Use waypoints | No announcement | Same on both |
| **Permission denied** | Sharing to team without the role that allows it | Private plan, editable; sharing to own paired devices | The role and what it allows | Ask the owner · Share with my paired devices instead | Player's own share action: focus stays in the share control, assertive reason | Tablet share attempt gets the same refusal and reason |
| **Failed** | Route model error; save error | Objectives, requirements, waypoints; unsaved edits kept as a draft | Time, component, reference | Retry route · Retry save | Player-caused: focus to the failure, assertive; otherwise polite | Draft kept on the device where it was made and labelled as unsynced |
| **Success** | Bundle, requirements and route available | All | Every requirement with its source and time; route estimate as modelled, with full model provenance and trade-offs | — | Share confirmation polite, naming the scope | Share scope explicit (only me, my paired devices, team); marks and plans carry author, device, time, scope and revision |

## Team

| State | Trigger | Usable remainder | Provenance shown | Recovery | Focus and announcement | Desktop and tablet sync |
| --- | --- | --- | --- | --- | --- | --- |
| **Empty** | Not in a team | Solo planning; marks on own devices; pairing a device | "Not in a team. Nothing is shared." | Create or join a team | No announcement | Pairing works without a team; a tablet never becomes a member |
| **Loading** | Connecting to the relay | Last known members shown with their last update time | "Connecting; last update 18:40" | Cancel | Polite after 1 s; "Connected" once | Tablet's own connection state separate from the desktop's |
| **Offline** | Relay unreachable | Local-network paired tablet; marks queue locally; last known team state with ages | "Relay offline since 18:31; 2 marks queued" | Retry now; queued marks send on reconnect in order | Polite once | Tablet and desktop continue over the LAN; queued operations carry revisions so reconnect cannot reorder them (**needs contract**: #276) |
| **Stale** | A member has not published within their threshold | Member shown as stale with last update time, not removed until the relay forgets them | "Stale: last update 2 min ago" | Ask them to reconnect | Polite once per member crossing the threshold | Same on both |
| **Partial** | Members share different amounts (position only, no loadout) | What each member chose to share | "Birch shares position only" | What is shared (explains the member controls it) | No announcement | Same on both |
| **Permission denied** | Clearing another member's marks, revoking a device, or inviting without the role | Own marks and own devices | The role and what it allows | Ask the owner | Player's own action: focus stays, assertive reason | The refusal is identical on tablet and desktop |
| **Failed** | Invite creation or revocation errors | Existing team and devices unchanged | Time, reference; "No device was revoked" when that is true | Try again | Player-caused: focus to the failure, assertive | Revocation state is never shown as done on one device and pending on the other; both show pending until confirmed |
| **Success** | Connected | All | Each member's role, device and freshness; each mark's author, device, scope, age and lifetime; each paired device's relationship and expiry | — | Polite for a member joining or leaving; never for position updates | Paired device shown as "your device, not a squad member" everywhere |

## Debrief

| State | Trigger | Usable remainder | Provenance shown | Recovery | Focus and announcement | Desktop and tablet sync |
| --- | --- | --- | --- | --- | --- | --- |
| **Empty** | No raids recorded | Explanation of how raids get recorded; import | "Raids are recorded from the game log while the companion runs" | How raids are recorded · Import | No announcement | Same on both |
| **Loading** | History or a raid's timeline loading | Raid list first; timeline when opened | "Loading raid history stored on this PC" | Cancel | Polite after 1 s | Tablet shows the desktop's history; it does not keep its own copy |
| **Offline** | No network | All local history, corrections, notes | "History is local" | Retry team export when back | Polite once, only if an export was attempted | Tablet over LAN unaffected |
| **Stale** | A newer model or catalog exists than the one the raid used | The raid as recorded, with the prediction preserved as shown | "Used traffic-sample-v0; a newer model exists and is not applied retroactively" | Compare with newer model (clearly a separate view) | Polite once when opened | Same on both |
| **Partial** | Outcome, extract or value not recorded or not observed | Everything known, each row labelled Observed, Inferred, Estimated or Manual; unknown stays unknown | "Outcome not recorded"; "Extracted at ZB-1011: inferred, not confirmed by the game" | Enter outcome · Confirm or correct inference | No announcement | Same on both |
| **Permission denied** | Export location not writable; team export without the role | History, corrections, local export elsewhere | Which location or role | Choose another location · Ask the owner | Player's own export: focus to the message, assertive | Same on both |
| **Failed** | Saving a correction or note fails | The correction kept as a visible draft; history unchanged | "Not saved; draft kept" with time | Retry save | Focus to Retry save, assertive "Correction not saved. Draft kept." | Draft labelled unsynced on the device where it was made; never shown as saved on the other |
| **Success** | History available | All | Every timeline row with how it is known and its source; carried value always "estimated at capture, not extracted"; prediction preserved with its model version | Undo for each correction | Correction saved: polite, focus to Undo | Corrections carry base revision; a concurrent correction on the other device raises a conflict rather than overwriting |

## Setup & Admin (Home readiness in Variant B)

| State | Trigger | Usable remainder | Provenance shown | Recovery | Focus and announcement | Desktop and tablet sync |
| --- | --- | --- | --- | --- | --- | --- |
| **Empty** | First launch, nothing configured | Sample data exploration; every workspace in sample mode, labelled | "Nothing configured yet"; "Sample data" on every page | Use sample data · Start Get ready | No announcement; first focus on the page heading | Pairing is offered only after setup, and the desktop pairing page says why; no tablet can be connected yet |
| **Loading** | Readiness checks running | Finished checks show their result as they complete | Each check's own time | Stop checks | Polite after 1 s; each result is not announced separately; a summary once ("5 checks done, 1 needs action") | Tablet shows the desktop's readiness summary, read-only |
| **Offline** | Game data cannot sync | Everything local, including capture, recognition from the cached catalog, and history | "Game data last synced 2026-09-13 20:05; next attempt 19:00" | Retry now | Polite once | Same on both |
| **Stale** | Game data, model or update check past their thresholds | Everything, labelled with ages | Last attempt, last success, next retry for each dependency | Sync now | Polite once | Same on both |
| **Partial** | Some checks passed, some need action, some optional | Everything that passed; optional items stay optional | "4 of 6 checks passed; 1 needs action; 1 optional" | Go to next action | No focus move; the header count updates silently and the Get ready heading carries the count | Same count on both |
| **Permission denied** | A folder unreadable; protected storage unavailable | Other checks and features that do not need it | Which folder or store, since when, what does not work because of it | Choose folder · Show diagnostic | For a folder the player just chose: focus stays on the chooser, assertive reason | Tablet shows the desktop's denial summary only; it cannot change desktop folders |
| **Failed** | Recognition self-test fails; sync errors | Map, plan, team and history; capture reports "text recognition unavailable" rather than analysing badly | Recogniser name, time, reference | Show diagnostic · Run self-test again · Report a problem with preview | Player-run self-test: focus to the result, assertive on failure | Same on both; capture from the tablet reports the same unavailability before arming |
| **Success** | All required checks passed | All | Each check with its own time and source; privacy lines stating exactly what happens to screenshots and diagnostics | — | Polite "Nothing in setup needs action" once, when the last action completes | Same on both |

## Capture and scan results (not yet specified)

Loot Scan, Stash Scan and the Capture area are the primary interaction (C-04), but this matrix gives
them no rows of their own: "recognition unavailable" appears only under Setup Failed. Their states
(no screenshot yet, analysing, prices cached, unresolved cells, folder denied, text recognition
unavailable) still need rows here, informed by [capture-intent-mismatch.md](capture-intent-mismatch.md)
and owned with #271, #282 and #283. The storyboard's Stash scan page therefore has no injected
workspace states in either variant.
