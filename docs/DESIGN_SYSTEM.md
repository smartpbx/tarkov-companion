# V2 design system

This is the isolated V2 foundation for a native desktop companion and its later tablet adapter.
It is not enabled in the V1 shell yet: #267 owns legacy-shell isolation, #270 owns persisted
appearance preferences, #279 owns packaged Windows and assistive-technology evidence, and #290
maps the shared meanings to tablet UI. It never controls Escape from Tarkov, reads its memory,
inspects its traffic, produces game input, tracks players, or renders an in-game overlay.

## Contract and ownership

[`semantic-manifest.v1.json`](../src/TarkovCompanion.App/Assets/V2/semantic-manifest.v1.json) is
the versioned, machine-readable contract shared across platforms. It carries semantic role and
state IDs, required wording keys, glyph/border redundancy, evidence fields, automation-name
templates, and responsive intent; it deliberately excludes Avalonia classes, CSS, breakpoints,
focus-ring pixels, screenshots, and game interaction. Avalonia resources are an adapter under
`Themes/V2`; a future tablet adapter independently maps the same manifest to semantic HTML/CSS.

The contract has explicit `unknown`, `unavailable`, `offline`, `stale`, `partial`, `denied`, and
`failed` states. Each is a word plus a non-colour cue (glyph or border/pattern) plus an automation
name, so colour alone never means ready. Historical, modelled, and potential material is compactly
identified and never presented as live detection. When freshness or confidence changes a decision,
it is inline; the full source, UTC timing, coverage, confidence/calibration, and model version live
in the adjacent **Why** disclosure.

## Tokens and variants

`V2Tokens.axaml` defines semantic canvas/surface/text/border/action/focus/status roles, type,
spacing, shape, elevation, focus, motion, density, target, map and chart roles. Dark, Light, and
HighContrast dictionaries use Avalonia theme selection; **System** means Avalonia follows the OS
and Windows contrast override selects `HighContrast` when composed by #270. The separate
colour-vision palette supplies red-green-safe, blue-yellow-safe, and monochrome mappings while
retaining word/glyph/border cues. Motion has System, Reduced, and Full values; reduced motion
removes transitions but never hides a state change.

Readable floors are H1 28/36, H2 22/28, H3 18/24, body 16/24, and labels/tables 14/20 DIP. Numeric
views use tabular figures in their eventual template. Density changes whitespace only, never type
size or required content. Desktop controls are at least 44 DIP and touch controls at least 48 DIP.

## Native primitives

`V2PrimitiveGallery` is a disconnected native example/fixture, not a feature page. It demonstrates
page heading and main landmark, banner, field error, state badges, empty state, card, semantic table
headers, toolbar, Why disclosure, paired-device summary, capture queue, desktop/touch targets, and
polite/assertive live regions. Its class contract also reserves shared capture stages
`armed → queued → processing → review → corrected/failed` and paired-device facts for mode,
lease, offline/reconnecting, acknowledgement lag, conflict, sharing scope, and recovery. Features
provide the current facts and actions; protocol/evidence history stays in Why or Setup & Admin.

Feature consumers use semantic resources only. They provide an automation ID, localized name and
help text, the expected landmark or heading level, logical source-order keyboard order, and visible
focus. Navigation focuses the page H1. A dialog focuses its title and restores the invoking control,
or the H1 if it disappeared. Background work never steals focus; coalesced background changes are
polite, while a refusal, the user's failed action, or a conflict is assertive.

Maps and charts expose an ordered data alternative. The semantic table is intentionally a primitive
contract rather than a DataGrid commitment: #279 must inspect the official Avalonia DataGrid on
packaged Windows for headers, cells, sorting, selection, keyboard navigation, 200% text, Narrator,
and NVDA before it can become the implementation choice.

## Adaptation and scaling

Adapt by effective content width after text scale, not device labels: narrow below 600 DIP, compact
600–899, standard 900–1279, expanded 1280 and above. At 200% text, layouts reflow sooner; only true
tables may scroll horizontally. The deterministic baseline matrix covers 100%, 125%, 150%, and 200%
text plus narrow desktop/tablet cases in
[`visual-baselines.v1.json`](../src/TarkovCompanion.App/Assets/V2/visual-baselines.v1.json).

The root scale migration is staged: freeze the V1 behavior; introduce V2 text scale and density
outside it; have #267 wrap only the legacy V1 host in its existing transform; keep V2 shell and
flyouts unscaled; migrate feature by feature; keep map zoom separate; then remove the root transform
only after no V2 content depends on it. For rollback, legacy 0.90 maps to 100%/Compact, 1.00 maps to
100%/Standard, and 1.15/1.30 remain custom text values; new choices are 100/125/150/200%.

## Language, locale, and RTL

The gallery contains no user-facing literals: default strings come from `V2Strings.axaml` and the
paired `strings.en.json`; `strings.qps-ploc.json` makes expansion failures visible. Formatting takes
an explicit `CultureInfo` and `TimeZoneInfo` for a whole date, number, or currency value; callers
must compose a complete localized message rather than concatenate fragments. #269 supplies the
actual language/locale/timezone context.

RTL is an explicit **deferred native validation decision**, not an unsupported assumption: V2 uses
logical start/end layout and direction-neutral status symbols, and #279 must exercise RTL text,
focus order, icon mirroring, mixed bidirectional data, and 200% text on packaged Windows before
the product can claim support.

## Evidence boundary

The fixtures assert native resource structure and stable expected tree/visual requirements in CI.
They are not screenshots, Windows UIA dumps, or claims that Narrator/NVDA/manual touch checks have
occurred. Those manual Windows gates remain #279; participant-sensitive navigation labels and
placement remain provisional under #265.
