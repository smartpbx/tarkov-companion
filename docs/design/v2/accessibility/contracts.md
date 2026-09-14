# Native primitive accessibility contract

## Names and structure

Every primitive receives a stable automation ID, a localized automation name, and localized help
text when its visible label is insufficient. A page provides one H1 and a named `Main` landmark;
header, navigation, search, and complementary regions are named only when present. Decorative
glyphs are not focusable or separately named.

Semantic tables expose a caption, column headers, row headers, and reading order that agrees with
the visual order. A chart or map has a text legend with word/pattern/series identity and an ordered
data alternative. No state or action depends solely on colour, position, hover, or motion.

## Keyboard and focus

Source order is keyboard order. The first focus stop of a full page is its skip link when one is
present; navigation ends at the H1. A dialog focuses its title, then first interactive control;
Escape closes without discarding; closing restores the invoking control or the H1. Before a
re-render replaces navigation or header, preserve the active control and unsubmitted text/selection
where possible. Background updates never move focus.

`Alt+Shift+C` is only a provisional, remappable companion-local Capture candidate; it must be
disableable because Windows may reserve it for input-language switching. Bare single-character
shortcuts are not part of V2.

## Announcements and material risk

One polite region announces coalesced background completion, queue arrival, or confirmation. One
assertive region announces a refused action, failed user action, or revision conflict. Routine
freshness/provenance is a compact badge or legend; escalate it to an inline recovery message or a
banner when it changes the decision, blocks an action, reflects active sharing, a security failure,
or a destructive consequence. Why/details holds complete evidence without reserving policy panels
on routine surfaces.

## Pending native gates

Automated assertions prove only the fixture's declared tree, strings, manifest, and baseline
coverage. #279 must verify UIA tree output, full keyboard operation, focus restoration, Narrator,
NVDA, Windows high contrast, 200%/320-effective-DIP reflow, native DataGrid behavior, actual touch
targets, colour-vision redundancy, and expected/actual/diff visual artifacts on packaged Windows.
