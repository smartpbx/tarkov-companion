# Design system

Two systems live side by side. The **V1 instrument shell** is the shipping application and is
implemented by `src/TarkovCompanion.App/Themes/InstrumentStyles.axaml`. The **V2 foundation** is an
isolated set of resources, styles, and a native gallery that no shipping page uses yet. Neither
controls Escape from Tarkov, reads its memory, inspects its traffic, produces game input, tracks
players, or renders an in-game overlay.

## V1 instrument shell

### Direction

The app is a cartographic instrument panel for a second monitor: map-first, information-dense, calm at rest, and emphatic only when an item or route needs immediate attention. It does not imitate Escape from Tarkov's visual chrome.

### Tokens

- Obsidian canvas `#11151B`
- Gunmetal surface `#1A212A`
- Cold steel text `#C6D0D8`
- Survey cyan `#56B8C6` for position/navigation
- Field ochre `#C6A15B` for economy/value
- Signal coral `#DF6A62` for risk/allergy
- Triage sage `#77B895` for confirmed safe/keep states

Inter is used deliberately for high legibility at monitor distance, with tabular figures for prices and time. Labels use sentence case; ratings always include text and never rely on color alone.

### Layout

```text
┌ status: evidence and freshness, never decoration ┐
├ compact rail ┬ map / primary workspace ┬ context ┤
│ navigation   │ wide, pan/zoom/layers    │ action  │
│              │                          │ reasons │
├──────────────┴ last observed scan / uncertainty ┤
```

The wide map/workspace anchors the eye. The context rail changes by selection rather than multiplying identical cards. Borders encode grouping and selected state; corner radii remain restrained. Motion is limited to evidence updates and user-triggered panel transitions.

The #266 pre-dispatch blueprint measured what V2 has to fix here: text down to 10–12 DIP,
faint-text pairs at 3.26–4.05:1, 26–38 DIP targets, a whole-window scale transform, and no
`AutomationProperties` anywhere in the V1 views. V1 stays as it is until #267 isolates it.

## V2 foundation

### Contract and ownership

[`semantic-manifest.v1.json`](../src/TarkovCompanion.App/Assets/V2/semantic-manifest.v1.json) is
the versioned, machine-readable contract shared across platforms. It names token roles, status
axes with their wording keys and glyph/outline cues, evidence fields, map-evidence and chart-series
identities, capture stages, paired-device states, and adaptation intent. It deliberately excludes
Avalonia classes, CSS, breakpoints, focus-ring pixels, screenshots, and game interaction. The
Avalonia adapter lives under `Themes/V2`; #290 maps the same manifest to tablet HTML/CSS.

A host merges one dictionary, `Themes/V2/V2Resources.axaml`, which combines:

| File | Holds |
| --- | --- |
| `V2ThemeVariants.axaml` | every brush and elevation shadow, per theme variant |
| `V2Tokens.axaml` | type sizes and line heights, spacing, shape, density, targets, focus, motion, glyphs |
| `V2Strings.axaml` | default English copy, identical to `Assets/V2/strings.en.json` |

`V2DesignSystemContractTests` fails if a manifest token has no resource or a resource has no
manifest token, so the lists above and the manifest cannot drift apart silently.

### Tokens

| Group | Roles |
| --- | --- |
| Colour | canvas, surface, surfaceRaised, textPrimary, textSecondary, border, action, focus, success, warning, danger, info, unknown |
| Chart | series1–series4, each also identified by an outline pattern (solid, dashed, dotted, dash-dot) |
| Map | historical, modelled, manual, unavailable, each with a wording key and a pattern |
| Type | heading1 28/36, heading2 22/28, heading3 18/24, body 16/24, label 14/20 DIP; nothing below 14 |
| Space | xs 4, sm 8, md 12, lg 16, xl 24 DIP, each as a gap (double) and an inset (Thickness) |
| Shape | control 6, card 10 DIP radius |
| Elevation | flat, raised, dialog shadows; empty in high contrast, and never a substitute for a border |
| Density | compact, standard, comfortable gaps and insets; type size and targets never change |
| Target | desktop 44, touch 48 DIP minimum |
| Focus | indicator thickness 3 DIP |
| Motion | full 160 ms, reduced 0 ms; reduced motion drops the transition, never the state change |

Density is applied by an ancestor class (`v2-density-compact`, `v2-density-comfortable`); no class
means standard. Numeric views still need tabular figures in their eventual templates.

### Theme variants

`V2Appearance.Resolve` turns a stored appearance preference (System, Light, Dark, HighContrast) and
colour-vision preference (Standard, red-green-safe, blue-yellow-safe, monochrome) into one Avalonia
`ThemeVariant`. An operating-system contrast theme wins over the in-app appearance choice. #270
persists the preferences and #267 applies the variant to a host.

Custom variants are static `ThemeVariant` instances that inherit Dark or Light, referenced from the
dictionary keys with `x:Static`. A colour-vision variant overrides only the five status brushes:
the decision triad (success, warning, danger) is re-chosen for the named deficiency, and info and
unknown become a neutral ink so neither can pass for a triad colour. Monochrome sets every status
brush to the primary text colour, leaving the word, glyph, and outline to carry the state.

What CI measures, from the dictionaries Avalonia actually loads:

- in every variant, text, action, and status colours at least 4.5:1 on canvas, surface, and raised
  surface, and border, focus, chart, and map colours at least 3:1 on the same three surfaces;
- in every variant except monochrome, the triad at least 40 CIE76 ΔE apart under typical vision;
- under each deficiency a variant claims (Machado 2009 simulation at full severity), the triad
  still 40 ΔE apart and info and unknown at least 15 ΔE from every triad colour. Red-green-safe
  claims protanopia and deuteranopia, blue-yellow-safe claims tritanopia, and high contrast, which
  has no colour-vision variants of its own, claims all three.

A simulation models typical dichromacy. It does not replace the human colour-vision review in #279.

### Status axes

The manifest keeps three independent axes, because one result can be partial and stale at once:

- **availability**: ready, unknown, unavailable, offline, permission denied, failed. Each has a
  word, a glyph, and an outline pattern.
- **completeness**: complete, partial, coverage unknown. Each has a word and a glyph.
- **freshness**: up to date, stale, age unknown. Each has a word and a glyph.

Every status names an availability value. A completeness or freshness default may be left unstated
only when the feature has evidence for it; otherwise it shows that axis's unknown value. The
automation-name template joins the three words before the detail. Historical, modelled, and
potential material is identified compactly and never presented as live detection; decision-changing
freshness or confidence is inline, and full source, UTC timing, coverage, confidence, and model
version live in the adjacent **Why** disclosure.

### Native primitives

`V2PrimitiveGallery` is a disconnected native example, not a feature page, and nothing in CI
instantiates, lays out, or captures it. It shows one synthetic state of each primitive:

- a page heading inside a named Main landmark;
- a state banner and availability badges, each showing a word, a glyph, a role colour, and an
  outline pattern;
- an evidence summary with a **Why** Expander;
- a labelled field with its error;
- a toolbar;
- an empty-state card and a touch target;
- a table;
- map and chart legends;
- paired-device and capture-queue cards;
- polite and assertive live-region text.

`V2PrimitiveContracts` records each primitive's gallery automation id. The dialog host is deferred
to #267: it needs the V2 shell's top-level window to place a dialog and restore focus to its
invoker.

The markup follows how Avalonia 12.1.2 maps automation properties to Windows UI Automation, as read
from its source:

- **Panels** get a `NoneAutomationPeer`, which is outside the UIA control view. A landmark, group
  name, or `ControlTypeOverride` on a panel therefore also sets `AccessibilityView="Control"`.
- **Toolbars** are named panels with `ControlTypeOverride="ToolBar"`, not Navigation landmarks.
- **Tables:** `IsColumnHeader` and `IsRowHeader` are documented as having no effect, and Avalonia
  implements no UIA table or grid pattern. The gallery table instead exposes a named group per row,
  whose name is the whole localized row sentence. Whether the official DataGrid can replace this is
  still the #279 decision gate: headers, cells, sorting, selection, keyboard navigation, 200% text,
  Narrator, and NVDA, all on packaged Windows.
- **Text boxes** take no name from a nearby label, and `LabeledBy` is not mapped to UIA LabeledBy.
  The visible label's resource is also set as `AutomationProperties.Name`.
- **Expanders** take no name from their header, so the Why disclosure sets its name explicitly.
- **Live regions:** `LiveSetting` does not inherit, and Windows raises LiveRegionChanged only when
  the element's own name changes. That is why the setting sits on the TextBlock a host rewrites.
- **Decorative glyphs and swatches** use `AccessibilityView="Raw"`.

Feature consumers use semantic resources only. They provide an automation id, localized name and
help text, the landmark or heading level, source-order keyboard order, and visible focus. Navigation
focuses the page H1. A dialog focuses its title, then restores the invoking control, or the H1 if
that control is gone. Background work never steals focus. Coalesced background changes are
announced politely; a refusal, a failed user action, or a conflict is announced assertively. Maps and
charts expose an ordered data alternative.

### Adaptation and scaling

Adapt by effective content width after text scale, not device labels: narrow below 600 DIP, compact
600–899, standard 900–1279, expanded 1280 and above. At 200% text, layouts reflow sooner, and only true
tables may scroll horizontally.
[`render-matrix.v1.json`](../src/TarkovCompanion.App/Assets/V2/render-matrix.v1.json) lists the
cases the gallery must be rendered and approved at: 100/125/150/200% text, narrow desktop and tablet
widths, and each theme variant. It is marked `unrendered`. It is the checklist #279's captures must
cover, not a baseline, and nothing has been rendered against it.

The root scale migration is staged:

1. Freeze the V1 behaviour.
2. Introduce V2 text scale and density outside it.
3. #267 wraps only the legacy V1 host in its existing transform.
4. Keep the V2 shell and flyouts unscaled.
5. Migrate feature by feature, keeping map zoom separate.
6. Remove the root transform only after no V2 content depends on it.

For rollback, legacy 0.90 maps to 100%/Compact and 1.00 to 100%/Standard, while 1.15 and 1.30
remain custom text values. The new choices are 100/125/150/200%.

### Language, locale, and RTL

The gallery contains no user-facing literals. `strings.qps-ploc.json` is generated from
`strings.en.json` by `V2PresentationFormatting.PseudoLocalize`: accented letters, brackets that
expose truncation, and 40% padding. A test compares the two files. Formatting takes an explicit
`CultureInfo` and `TimeZoneInfo` for a whole date, number, or currency value. Callers compose a
complete localized message rather than concatenating fragments. #269 supplies the actual language,
locale, and timezone context.

RTL is an explicit **deferred native validation decision**, not an unsupported assumption. V2 uses
logical start/end layout and direction-neutral status symbols, and #279 must exercise RTL text,
focus order, icon mirroring, mixed bidirectional data, and 200% text on packaged Windows before the
product can claim support.

### Evidence boundary

Automated in CI today, by `V2DesignSystemContractTests` and `V2ThemeResourceTests`:

- manifest-to-resource parity;
- compiled dictionary loading and variant inheritance;
- the contrast and simulated colour-vision floors above;
- token types;
- string, pseudo-locale, and resource-key parity;
- the gallery's declared headings, landmarks, names, live-region placement, patterns, and the
  absence of the ineffective header properties;
- the resolver rules.

Still open, and not claimed by anything in this directory:

- rendering the gallery at any scale or width;
- visual-regression captures and their approval;
- a UIA tree dump;
- full keyboard operation and focus restoration;
- Narrator and NVDA;
- the Windows contrast-theme result;
- 200% text and 320-effective-DIP reflow;
- the DataGrid decision;
- real touch targets;
- human colour-vision review;
- RTL.

Those belong to #279. Participant-sensitive navigation labels and placement remain provisional
under #265.
