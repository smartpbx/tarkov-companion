# V2 experience validation (#265)

> **Status: not yet run.**
> No participant has taken part in a session. This directory has no findings, task times,
> success rates, or validated decisions in it. It holds the method, the materials, and blank
> places to record evidence. The #265 acceptance gate stays open until real sessions fill those
> places.

This package gets the #256 five-workspace model and the capture-first workflow in front of
representative internal players **before** native code (#267 onward) freezes navigation. It is
product and UX groundwork only: it changes no `src/**` code, overwrites none of the concept renders,
and changes none of the #264 contracts.

## What is here

| File | Purpose |
| --- | --- |
| [research-plan.md](research-plan.md) | Questions, hypotheses, method, session structure, roles, and the completion gate |
| [participant-screening.md](participant-screening.md) | Who counts, required coverage, the screener, private-roster rules and public aggregate coverage |
| [consent-and-data-handling.md](consent-and-data-handling.md) | Consent script, what is recorded, where it lives, retention and withdrawal |
| [metrics-and-analysis.md](metrics-and-analysis.md) | What is measured, how it is coded, how two variants are compared honestly at n = 5 |
| [severity-rubric.md](severity-rubric.md) | S0 to S4 finding severity, with truthfulness and accessibility floors |
| [navigation-variants.md](navigation-variants.md) | Variant A (workspace rail) and Variant B (workflow hub), and the findability tasks both get |
| [journeys.md](journeys.md) | The six moderated journeys, with scripts, expected paths, and comprehension probes |
| [accessibility-flows.md](accessibility-flows.md) | Annotated keyboard, screen-reader, touch, reflow, contrast, text-size, motion, map/list and focus flows |
| [state-matrix.md](state-matrix.md) | Eight states for each of the six destinations (five workspaces plus Setup & Admin): trigger, remainder, provenance, recovery, focus, sync |
| [capture-intent-mismatch.md](capture-intent-mismatch.md) | Deterministic flow when armed intent and detected context disagree, plus still-writing, duplicate, unknown, and device-race cases |
| [render-audit.md](render-audit.md) | Audit of the eight concept renders in `docs/design/v2/` |
| [revision-brief.md](revision-brief.md) | What the next concept pass must change or add |
| [validation-report.md](validation-report.md) | Blank report to fill after sessions |
| [acceptance-fixture-map.md](acceptance-fixture-map.md) | Draft journey fixtures and the implementation issues that consume them |
| [templates/](templates/) | Blank evidence templates: screener response, consent record, session log, finding, findings register, accessibility addendum, moderator checklist |
| [prototype/](prototype/) | Semantic HTML/CSS/JS storyboards for both variants |
| [../decision-log.md](../decision-log.md) | Accepted, rejected, deferred and open hypotheses, with rationale |

## What the storyboards prove, and what they cannot

The storyboards in [prototype/](prototype/) test **vocabulary and flow**. They specify real landmarks,
headings, accessible names, focus order, native dialogs, live regions, keyboard operation, touch
target minima, reflow, forced-colours support, reduced motion, and a list alternative for every map.
They are built so keyboard, screen-reader, magnifier and touch participants can take part in the
navigation study rather than being excluded from it. They have not yet been re-run through the full
exact-head browser matrix or used by an assistive-technology participant; the pilot and each
participant's pre-session setup check must confirm the applicable flow.

They **do not** prove:

- that the Avalonia application will be accessible. Web ARIA is not UI Automation, and a screen
  reader on a browser page behaves differently from Narrator on a native window. #266 and #279 own
  that evidence;
- visual fidelity. The palette exists only so contrast and focus visibility are realistic;
- recognition quality, recommendation correctness, model accuracy, latency, or sync performance.
  Every screenshot outcome is simulated by the moderator, and every number is sample content.

They make no network request, read no user or game file, and never press a game key. They load only
the bundled storyboard HTML, CSS and sample-content JavaScript. Open
[prototype/index.html](prototype/index.html) directly from a checkout.

### Participant distribution scope

The distributable unit is the repository's **`docs/` tree**, served from the repository root (or
copied intact with its relative paths), not `prototype/` by itself. It contains the storyboard files,
the journey and research links opened from the index, and the Safety/data-methodology links surfaced
from Setup. A moderator may use a local static server rooted at the checkout and give a participant
`/docs/design/v2/validation/prototype/index.html`; no server is required when the same `docs/` tree is
opened locally. Before distribution, run `node docs/design/v2/validation/prototype/link-audit.mjs` at
the repository root. It audits every static participant asset/document edge and the dynamically
generated Safety/data-methodology edges in both variants, resolves real paths, and fails if any
target is missing or escapes the canonical `docs/` bundle. Run
`node docs/design/v2/validation/prototype/storyboard-audit.mjs` as the targeted headless guard; set
`CHROMIUM_PATH` only when Chromium is not on `PATH` or in the local Playwright browser cache. It
crawls the routes and generated links in both variants and exercises the correction, retry, skip and
ordered-position regressions. Neither check is participant evidence.

## Fixed product boundary

Every artifact here keeps the #256 boundary. The companion never reads or writes EFT process
memory, never injects code or hooks the game, never inspects EFT network traffic, never generates
gameplay input or automates anything in the game, never tracks enemies live (no ESP, no radar), and
never draws an in-game overlay. Capture is started by the player with the game's own screenshot key
or a file they choose, and it controls only the companion. Historical or modelled traffic appears
only with source, timestamp, coverage, confidence and model version, and is never presented as a
live detection.

## When this package is done

#265 is complete only when every item in the completion gate in
[research-plan.md](research-plan.md) section 8 is met, with the evidence in
[validation-report.md](validation-report.md). In short: real sessions that meet the coverage in
[participant-screening.md](participant-screening.md), every named gate-controlled journey **step**
and findability task meeting its per-variant minimum (a recorded non-attempt does not count),
each variant primary for at least two people, accessibility flows exercised by daily users of the
technology, findings rated with [severity-rubric.md](severity-rubric.md) and every S0 and S1 owned,
each hypothesis moved in [../decision-log.md](../decision-log.md) with evidence, and
[acceptance-fixture-map.md](acceptance-fixture-map.md) revised from those decisions. Until then
this pull request is groundwork, not a result.
