# V1 parity ledger

Every capability the V1 shell had, where it lives in V2, and what proves it.

V2 is the shell a plain launch opens. V1 is still here, behind `--ui-shell legacy`, and is the
fallback when V2 misbehaves. So "parity" here is not a slogan: it is the list somebody can read
before deciding whether a V1 path is safe to delete, and the list Clayton can read to find out
where something he used yesterday has gone.

**How to read a row.** *Carried* means the same capability, in a V2 surface, over the same engine.
*Replaced* means V2 does the job a different way and the V1 way is not coming back. *Not carried*
means it is genuinely absent from V2 today, with the issue that owns it. Evidence is a test class
or a page-gallery shot, named so it can be run; a claim with no evidence column is not in this file.

Two evidence sources recur:

- **Unit tests** in `tests/TarkovCompanion.UnitTests`, run by `scripts/test.sh` and by the `linux`,
  `checks` and `windows-build` jobs.
- **The Windows page gallery**, `scripts/windows-page-gallery.ps1`, which launches the packaged
  application, opens each address, photographs it at 1920x1080 and 3840x1080, and fails on a
  missing automation id, a wrong heading, a blank required body, a binding warning or a control
  outside the window. It runs in `windows-verify`, which is a **required** check on `main` as of
  2026-09-19 (#279) — so every row whose evidence is a gallery shot is a row a merge can fail on.

## The fourteen V1 pages

V1's navigation has fourteen entries. All fourteen have a V2 home, and none of them is a V1 page
hosted inside V2 any more: the route table has no way left to say "draw a V1 page here" (#294
removed `V2RouteContent.LegacyPage` and `V2RouteDefinition.LegacyPage`).

| V1 page | Lives in V2 as | State | Owner | Proved by |
| --- | --- | --- | --- | --- |
| Raid | `#/raid` — `RaidCockpitView`: the V2 map renderer, raid timer, extracts, marks, objectives, manual corrections (side, clock, offered exits) | Replaced | #286 | `RaidCockpitExtractRowsTests`, `RaidCockpitObjectivesTests`, `RaidCockpitLiveLayersTests`, `RaidCockpitMarksLayerTests`, `RaidCockpitArtworkAndFloorsTests`, `RaidMapSpaceTests`, `RaidMarkGestureTests`, `RaidManualCorrectionsTests`; gallery `v2-a-raid-1920`/`-3840`, which also holds the map card to at least 0.66 x 0.75 of the window |
| Extract click (issue #3: name, faction, conditions) | Hovering or selecting an extract/transit marker on the Raid map, or pressing its row in Extract options | Replaced | #594 | `MapExtractSelectionDetailTests`, `RaidCockpitExtractRowsTests` |
| Scanner | `#/raid/loot` — `LootScanView`: one frozen capture's take/swap/leave decisions | Replaced | #282 | `LootScanDecisionServiceTests`; gallery `v2-a-raid-loot-1920`/`-3840` |
| Items | `#/intel` — `IntelWorkspaceView`: search, results, selected item, prices, needs | Replaced | #287 | `V2IntelWorkspaceSelectionTests`, `IntelResultOrderingTests`; gallery `v2-a-intel-*`, which asserts `v2-intel-results` and forbids a context column for an unresolvable item |
| Ammo | `#/intel/ammo` — `AmmoWorkspaceView` over V1's `AmmoPageViewModel` | Carried | #285 | `AmmoWorkspaceViewModelTests`; gallery `v2-a-intel-ammo-*`, asserting `v2-ammo-search` and that `v2-ammo-reload` is inside the window |
| Keys | `#/intel/keys` — `KeysWorkspaceView` over V1's `KeysPageViewModel` | Carried | #287 | `KeysAndFleaWorkspaceViewModelTests`; gallery `v2-a-intel-keys-*` |
| Flea | `#/intel/flea` — `FleaWorkspaceView` over V1's `FleaPageViewModel` | Carried | #284 | `KeysAndFleaWorkspaceViewModelTests`; gallery `v2-a-intel-flea-*` |
| Quests | `#/plan` — `PlanWorkspaceView` | Replaced | #288 | `PlanWorkspaceViewModelTests`, `PlanQuestRulesTests`; gallery `v2-a-plan-*` |
| Hideout | `#/plan/hideout` — `HideoutWorkspaceView` | Replaced | #307 | `HideoutWorkspaceViewModelTests`; gallery `v2-a-plan-hideout-*`, asserting `v2-hideout-status` |
| Loadout | `#/plan/loadout` — `LoadoutWorkspaceView` over V1's `LoadoutPageViewModel` | Carried | #288 | gallery `v2-a-plan-loadout-*`, asserting `v2-loadout-search` and that `v2-loadout-clear` is inside the window; `LoadoutBoardTests` (#501) |
| Events | `#/plan/events` — `EventsWorkspaceView` over V1's `EventsPageViewModel` | Carried | #288 | gallery `v2-a-plan-events-*`, asserting `v2-events-new-name` and that `v2-events-create` is inside the window; `EventScheduleTests` (#501), `EventRuleEditorTests` (#790) |
| Squad | `#/team` — `TeamWorkspaceView`: presence, marks, sharing, paired devices in one place | Replaced | #289 | `TeamWorkspaceViewModelTests`, `PairingInPlaceTests`; gallery `v2-a-team-*` |
| Group | `#/team/group` — the same `TeamWorkspaceView`, reached as a section | Replaced | #289 | `TeamWorkspaceViewModelTests`; gallery `v2-a-team-group-*` |
| History | `#/debrief` — `DebriefWorkspaceView`, including replay back onto the raid map | Replaced | #291 | `DebriefWorkspaceViewModelTests`; gallery `v2-a-debrief-*` |
| Settings | `#/setup` — `V2SetupWorkspaceView`, eleven sections (Overview, Game & Profile, Recognition, Data, Progress, Team & Devices, Updates, Privacy, Appearance, Displays, Diagnostics) | Replaced | #292 | `V2SetupWorkspaceViewModelTests`, `SetupSelfTestViewModelTests`; gallery `v2-a-setup-*` and `shell-v2-a` |

`V2ShellRegistryTests.Every_V1_page_name_still_opens_something` reads those fourteen names out of
V1's own navigation list and requires each to resolve to an address in both V2 variants, so a
fifteenth V1 page — or a renamed one — fails rather than shipping unreachable. That reading is
what found the one page with no alias: `--page Settings` was fatal under the default shell while
the other thirteen resolved (#294 added it).

## V2 has these, and V1 never did

| V2 surface | What it is | Proved by |
| --- | --- | --- |
| `#/intel/stash` (#283) | Stash scan: read a stash screenshot into a keep/sell list | `StashScanWorkspaceViewModelTests`, `StashScanWorkflowTests`, `GuidedStashScanEndToEndTests`, `RealStashFrameMeasurementTests`; gallery `v2-a-intel-stash-*` |
| `#/plan/keep` (#402) | A computed keep list over the same requirement catalog Hideout reads | `KeepListWorkspaceViewModelTests`; gallery `v2-a-plan-keep-*` |
| `#/tablet` (#290, #407) | The paired tablet's own view, and the link to reach it | `PairingInPlaceTests`; gallery `v2-a-tablet-*`, `shell-v2-a-tablet-link` |
| `#/intel/item/{id}` (#287) | One item's facts with their source and age, as an address | gallery `v2-a-intel-item-*` |
| `#/home` (variant B, #267) | Readiness and Continue as a landing page | `V2HomeOverviewViewModelTests` |

## Chrome and cross-cutting capabilities

| V1 capability | In V2 | State | Owner | Proved by |
| --- | --- | --- | --- | --- |
| Status bar: map, raid, time left, last position, observation/data/scan health | The V2 header: map picker, profile, raid elapsed, data age, and one health pill that opens the health dialog | Replaced | #267 | `V2ShellStateTests`; gallery `shell-v2-a` |
| Window position, size and monitor memory | `V2ShellWindowPlacement` plus `MainWindow.RestoreLayout`'s preview branch | Carried | #267 | `V2ShellPreviewStoreTests` |
| Interface scale (Ctrl +/-/0, and Settings' Smaller/Larger/Reset) | Setup › Appearance, and the same three chords | Carried, **fixed by #294** | #266 | Rendered at 130% under V2 with `tools/V2RenderPreview --interface-scale 130`. Until this change the transform lived inside the V1 host: the buttons were on screen under V2 and moved a number nothing applied |
| Replay a recorded raid onto the map | Debrief raises `ReplayRequested`; the shell opens it on the raid map and navigates there | Carried | #291 | `DebriefWorkspaceViewModelTests`; `V2ShellViewModel.WatchRaidAsync` |
| Ctrl+1..9 to reach a page | V2's own chord table (`V2ShellViewModel.HandleKey`), which is variant-aware | Replaced | #265 | `V2ShellCommandTests`; `V2ShellHostContractTests.Preview_lifecycle_keyboard_and_focus_stay_inside_the_preview_boundary` |
| `/` to focus the page's search box | The header search, or the workspace search in variant A's Intel | Replaced | #265 | `V2ShellHostContractTests` |
| Escape dismisses the map's selection | The shell's own dismiss, through the same chord table | Carried | #265 | `V2ShellCommandTests` |
| A notice dot on the rail when an update is waiting | The Setup destination carries a notice mark while a build waits (`V2ShellViewModel.MarkWhileUpdateWaits`) | Carried, **by #501** | #280 | `V2UpdateNoticeTests` |

## What #294 removed, exactly

Nothing a player can reach was removed. What went was code that could no longer be reached:

1. **`V2RouteContent.LegacyPage` and `V2RouteDefinition.LegacyPage`.** A route could name a V1 page
   and draw it unchanged. No route had named one since package 28, so the field was a way to
   reintroduce a V1 passthrough and nothing else. The registry's "must name a V1 page exactly when
   it hosts one" rule went with it, having nothing left to check.
2. **`V2ShellViewModel.LegacyPage`, `ShowsLegacyPage` and `SynchronizeLegacyRoute`.** The property
   that handed V1's current page to the V2 shell, the flag that showed it, and the method that kept
   V1's navigation in step with V2's. All three were driven by (1).
3. **The `ContentControl` in `V2ShellView.axaml` that drew it.** The page inset it carried
   (`Margin="18,6,18,12"`) already lives inside each workspace's own markup, which
   `V2ShellHostContractTests` checks.
4. **The V1 shell's construction on a V2 launch.** This is the one with a cost. V1's whole visual
   tree was written inline in `MainWindow.axaml` and hidden with `IsVisible="False"` under V2:
   fourteen pages built, every binding attached, nothing ever drawn. It now lives in
   `Views/Pages/LegacyShellView.axaml` and is realised through a template whose content is null
   unless V1 is the shell. `tools/V2RenderPreview` prints which happened:

   ```
   $ V2RenderPreview --ui-shell legacy   →  V1 chrome: built
   $ V2RenderPreview                     →  V1 chrome: not built
   ```

**The V1 shell itself was not removed and is not scheduled to be.** `--ui-shell legacy` opens the
same fourteen pages it always did; the render above is identical to the one taken before the move.
V1's page view models are not duplicates either — five V2 workspaces (Ammo, Keys, Flea, Loadout,
Events) are V2 views over exactly those view models, so deleting them would delete V2 features.
What was duplicated was V1's *chrome*, and chrome is what stopped being built.

## Gaps this ledger will not paper over

Checked against `main` on 2026-09-24. Closed since the list was written (2026-09-19):

- ~~Loadout and Events have no unit tests of their own.~~ `LoadoutBoardTests` and
  `EventScheduleTests` (#501), `EventRuleEditorTests` over `EventsPageViewModel` (#790).
- ~~No update notice outside Setup.~~ The rail's Setup destination is marked while a build waits
  (#501; `V2UpdateNoticeTests`).
- ~~`V2Appearance.Resolve` has no caller.~~ `V2AppearanceApplier` calls it and sets the
  application's theme variant, so light and high contrast can be chosen in Setup › Accessibility
  (#485; `V2AppearanceApplierTests`). Renders: `docs/design/v2/validated/`.

Still open:

- **Interface scale tops out at 130%.** `ShellLayout.Scales` is still `[0.9, 1.0, 1.15, 1.3]`.
  Text alone reaches 200% from Setup › Accessibility › Text size (#485, `WorkspacePreferences.TextScales`),
  and 150%/200% of the whole window only through the operating system's display scaling. Owner: #266.
- **Variant B is still shipped.** Two V2 navigations exist behind `--ui-shell v2-a|v2-b`. #265
  closed on 2026-09-17 without retiring either, so this has no open owner.
