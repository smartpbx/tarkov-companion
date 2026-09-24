# V2 visual specification: revisions

Home for issue #312. **The revision set the issue asks for does not exist yet.** This directory holds
what could be honestly produced today instead, and records what is blocked and by what. The original
nine concept renders in `docs/design/v2/*.png` are untouched and remain the historical evidence.

## What #312 asks for, and where each part stands (2026-09-19)

| Acceptance item | State | Why |
| --- | --- | --- |
| New image2 revision set, originals preserved | **Not produced** | No image generation is available to the workers; the originals are untouched. |
| Navigation applied only as #265's participant result supports | **Not applicable yet** | `validation/validation-report.md` still reads "Status: not yet run", and D-03 in `decision-log.md` ("what do revised concept images show for navigation?") is a proposed deferral. No participant decision exists to apply. The app ships Variant A (rail: Raid / Intel / Plan / Team / Debrief), judged against the original concepts, not against sessions. |
| Degraded-state sheet | **Partly**: see below | One degraded state can be rendered (Setup › Diagnostics with a mix of working and not-working checks). It is not committed here (see the note under the table). |
| Light, high-contrast and 200%-text variants | **Done, at 1920x1080** (2026-09-24) | Setup › Accessibility sets them since #485; captured below from renders made for #774. (On 2026-09-19 this read "cannot be captured": nothing applied the preference then.) |
| Narrow-window variant | **Done, at 1024x768** | Captured below, and it found real faults. |
| Keyboard-focus and touch/tablet variants | **Not produced** | `tools/V2RenderPreview` cannot drive focus, and the tablet is a browser page served by the relay, not a window it renders. |
| Correct render-audit findings RB-01 to RB-20 in the images | **Not done** | There is no revised set to correct; the findings stand in `validation/render-audit.md`. |
| Expert design/accessibility review recorded | **Not done** | Needs a person. Nothing here should be treated as reviewed. |

The sweep's own recommendation for #312 was to use renders of the running V2 as the specification
instead of a second image-generation pass. This directory starts that, for the one variant the
tooling can drive that the earlier photographs did not cover.

## Narrow window, 1024x768

Rendered with `tools/V2RenderPreview --ui-shell v2-a --width 1024 --height 768` on the synced catalog
(5,321 items, public game data) and the tool's demo raid, at `main` `9dc568b9`. Each picture was opened
and read before anything below was written about it. 1920x1080 remains the size to design for; this
is the check that nothing breaks below it.

| Capture | Route | Alt text | Issue |
| --- | --- | --- | --- |
| [raid](captures/narrow-1024x768/raid-1024x768.png) | `raid` | Raid tab on Customs. The map card fills under half the width; about thirty extract icons overlap heavily on the small map; the layer strip wraps to two rows; the "Layers · 9 on" chip is cut off at the right edge of its panel. The Raid plan panel with extract options sits at the right. | #286 |
| [intel-ammo](captures/narrow-1024x768/intel-ammo-1024x768.png) | `intel/ammo` | Ammo tab: caliber list on the left, ammo detail (".45 ACP AP", "Against armor" class 1 to 6 ratings, "Round" tier) on the right, and between them a strip about 45 px wide holding the words "Beats", "Order" and "5 ro u" one to a few characters per line. | #287 |
| [stash](captures/narrow-1024x768/stash-1024x768.png) | `intel/stash` | Stash scan with the guided-capture panel on the left, a strip about 45 px wide showing "Stas" and a thumbnail, and the Sort plan tiles (Keep, Sell, Use soon, Review 17) on the right. | #287 |
| [plan](captures/narrow-1024x768/plan-1024x768.png) | `plan` | Quests tab: the quest list and filters on the left, then a strip about 45 px wide in which "Nothing planned yet. Active quest objectives appear here, grouped by map." wraps at one to three characters per line, then an empty objectives pane reading "Pick a map to see its objectives." | #288 |
| [team](captures/narrow-1024x768/team-1024x768.png) | `team` (demo group) | Team tab: three members (two live, one stale) and a Connected chip, the shared-plan map with four numbered waypoints, and the right column of team quests, party and marks. The "Open shared plan" button sits over the bottom of the marks list. | #289 |
| [debrief](captures/narrow-1024x768/debrief-1024x768.png) | `debrief` | Raid history: the title "Raid history" collides with "Refresh", "4 raids", "Export CSV" and "Export JSON"; table columns are cut to "Mo…", "Dur…", "Not rec…", "Load time unk…" and dates read "9/19/20…". Raid detail with screenshots, flea sales and quests sits at the right. | #291 |

Not committed: the Setup › Diagnostics capture (`--selftest-demo`, four working and three not working).
The demo fixture writes a Windows username into its folder paths, and this repository is public. The
capture did show that the self-test still prints times as "2026-09-08 17:43 UTC" on `main`, which is
what PR #447 (player-local time) fixes.

### What the narrow captures found

1. **A pane collapses to a sliver.** On Plan, Intel › Ammo and Intel › Stash scan, the three-column
   layout gives one column about 45 px at 1024 wide, so its text stacks a few characters per line
   ("Nothing planned yet…" on Plan is unreadable). A column that cannot be readable at a width should
   hide or move, not squeeze.
2. **Debrief's header does not fit.** Title and actions overlap, and the history table truncates every
   column but the map.
3. **Raid is map-poor when narrow.** The map keeps its aspect ratio (correct) but ends up small enough
   that extract icons overlap; the bottom layers chip is clipped.
4. **Team's action button covers content.** "Open shared plan" overlays the last marks visible.

None of these was fixed here: this issue owns documentation only, and a layout fix belongs with the
surface's own issue. They are the concrete input for the follow-up the sweep proposed
("capture degraded-state, high-contrast, 200%-text, narrow-width and tablet renders of the running V2").

## Light, high contrast and 200% text, 1920x1080

Rendered with `tools/V2RenderPreview --ui-shell v2-a --width 1920 --height 1080` on the seed catalog
(public game data) with the tool's demo raid and demo squad (Geo, Riley, Sam are the tool's fixture
names), for #774 on 2026-09-23. Each picture was opened and read before it was described here, and
none shows a player name, profile or path.

| Capture | Route | Alt text |
| --- | --- | --- |
| [setup-light](captures/appearance-1920x1080/setup-light.png) | `setup` › Accessibility | Light theme selected: white panels, dark text, the theme, status-colour, text-size and spacing choices, the live preview strip and the keyboard-shortcut list. |
| [setup-high-contrast](captures/appearance-1920x1080/setup-high-contrast.png) | `setup` › Accessibility | High contrast selected: black background, white text, white-outlined buttons, cyan selection underline, blue Ready and red Failed chips. |
| [setup-200](captures/appearance-1920x1080/setup-200.png) | `setup` › Accessibility | Dark theme, text at 200%: the Setup tabs wrap to two rows; every heading and choice is twice the size and still inside the panel. |
| [raid-light](captures/appearance-1920x1080/raid-light.png) | `raid` (demo raid, Customs) | Light chrome around the Customs map with the modelled-traffic heat overlay, the route line, three squad members and the extract list; Crackhouse, Repair Shop and Warehouse 17 are dark text on a light halo. |
| [raid-high-contrast](captures/appearance-1920x1080/raid-high-contrast.png) | `raid` (demo raid, Customs) | Black chrome with white-outlined cards around the same map; place labels read white on a black halo over the heat overlay. |
| [raid-200](captures/appearance-1920x1080/raid-200.png) | `raid` (demo raid, Customs) | Text at 200%: the Raid plan panel wraps each fact to two or three lines and the bottom map toolbar wraps its chips; the map keeps its size. |
| [intel-light](captures/appearance-1920x1080/intel-light.png) | `intel --search graphics` (demo results) | Light Intel: two results, Graphics Card ₽1,200,000 selected and Graphics card ₽322,222, with the Graphics Card detail, key info and prices. |
| [intel-high-contrast](captures/appearance-1920x1080/intel-high-contrast.png) | `intel --search graphics` (demo results) | The same in high contrast: white text on black; the result list, the detail pane, each row and each side card have a white edge. |
| [intel-200](captures/appearance-1920x1080/intel-200.png) | `intel` (demo results) | The same results with text at 200%; the header, tabs, filters and rows all grow and nothing is clipped. |

### What the appearance captures found

1. **Light theme, Raid:** the place labels "Warehouse 17" and "Repair Shop" are pale text over the
   light heat overlay and are close to unreadable. The dark and high-contrast labels read.
2. **High contrast, Intel:** the result list and the detail pane lose their panel edges, so the list
   floats on black with nothing marking where it ends.
   Both fixed for #266 (2026-09-24): place names are drawn over a halo of the ground colour, and a
   pane's edge is the `divider` role, white in high contrast. The light and high-contrast captures
   above were re-rendered after the fix.
3. **200% text:** nothing is clipped on these three routes, but the Raid plan panel's facts wrap to
   three lines each, so only half as many fit above the fold.

## What would unblock the rest
- A decision on #265 (real participant sessions, or an explicit decision to proceed without them) so the
  navigation model can be applied or set aside.
- A person for the expert review, and for a tablet capture from a real paired tablet.
