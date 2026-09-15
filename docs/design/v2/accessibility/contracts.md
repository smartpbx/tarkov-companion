# Native primitive accessibility contract

## Names and structure

Every primitive receives a stable automation ID on an element in the UIA control view, a localized
automation name, and localized help text when its visible label is insufficient. A contract test
checks every automation id in the gallery for both. A page provides one H1 and a named `Main` landmark;
header, navigation, search, and complementary regions are named only when present. A toolbar is a
ToolBar control, not a Navigation landmark. Decorative glyphs and swatches are not focusable and are
kept out of the control view.

A table exposes a caption and header-to-cell relationships. Where the platform cannot, each row is
also exposed as one complete localized sentence. A chart or map has a text legend with a word and a
pattern for each series or evidence class, plus an ordered data alternative; the gallery does not
yet demonstrate that alternative (open #266 work). No state or action depends solely on colour,
position, hover, or motion. A banner's tone is a word, a glyph, and an outline pattern as well as a
colour, and the banner is a named group whose name carries the tone and the title.

## What Avalonia 12.1.2 actually maps

These were read from the Avalonia 12.1.2 source (`AutomationProperties.cs`, `ControlAutomationPeer.cs`,
`NoneAutomationPeer.cs`, and the Windows `AutomationNode.cs`). They explain choices that would
otherwise look arbitrary:

| Property or control | Behaviour | Consequence in V2 |
| --- | --- | --- |
| `IsColumnHeader`, `IsRowHeader` | documented "currently has no effect"; no UIA table or grid pattern exists | not used; rows are named groups; a test rejects the properties |
| Panels (`StackPanel`, `WrapPanel`, `Grid`, `Panel`, `Border`) | `NoneAutomationPeer`, excluded from the control view | an automation id, landmark, name, or control type on a panel also sets `AccessibilityView="Control"` and a name |
| `HeadingLevel`, `LandmarkType` | mapped to UIA; Banner, Complementary, and Region become custom landmarks | headings on TextBlocks; one Main landmark |
| `ControlTypeOverride` | honoured by every control peer | toolbar uses `ToolBar` |
| TextBlock name | always its text; `AutomationProperties.Name` is ignored | fuller names go on the containing group |
| `LabeledBy` | not mapped to UIA LabeledBy; only a name fallback | a text box's name is set from the visible label's resource |
| Expander | ExpandCollapse pattern; name not taken from `Header` | the Why disclosure sets its name |
| `LiveSetting` | not inherited; LiveRegionChanged fires only when that element's name changes | set on the TextBlock whose `Text` a host rewrites |

## Keyboard and focus

Source order is keyboard order. The first focus stop of a full page is its skip link when one is
present. Navigation focuses the H1. A dialog focuses its title, then its first interactive control.
Escape closes a dialog without discarding input. Closing restores focus to the invoking control, or
to the H1 if that control is gone. Before a re-render replaces navigation or a header, preserve the
active control and any unsubmitted text or selection where possible. Background updates never move
focus. The dialog host itself is deferred to #267.

The visible focus indicator on a V2 text box or button is drawn on the Fluent 12.1.2 template part
(`Border#PART_BorderElement`, `ContentPresenter#PART_ContentPresenter`), because Fluent's
TextBox `:pointerover`/`:focus` and Button `:pointerover`/`:pressed` styles set those parts directly
and would otherwise replace a border set on the control. A field's error border is declared before
its focus border so focus wins while both apply; the error sentence and help text keep stating the
error.

`Alt+Shift+C` is only a provisional, remappable companion-local Capture candidate; it must be
disableable because Windows may reserve it for input-language switching. Bare single-character
shortcuts are not part of V2.

## Announcements and material risk

One polite region announces coalesced background completion, queue arrival, or confirmation. One
assertive region announces a refused action, failed user action, or revision conflict. Routine
freshness/provenance is a compact badge or legend. Escalate it to an inline recovery message or a
banner, with the Information, Warning, or Critical tone from the manifest, when it:

- changes the decision;
- blocks an action;
- reflects active sharing;
- reports a security failure;
- carries a destructive consequence.

Why/details holds the complete evidence, so routine surfaces need no standing policy panels.

## Pending native gates

The automated assertions prove the gallery's declared structure, the compiled resources and their
measured contrast, and parity between strings, manifest, and render matrix. #279 must verify on
packaged Windows:

- the UIA tree output and full keyboard operation;
- focus restoration;
- Narrator and NVDA, including whether they announce the row groups, banner and card group names,
  and live regions as intended, and whether the group names make reading verbose;
- the Windows contrast-theme result and 200%/320-effective-DIP reflow;
- the native DataGrid alternative;
- actual touch targets;
- human colour-vision review;
- expected, actual, and diff visual artifacts, including the focus and error borders over the Fluent
  templates and the banner glyphs' font fallback.
