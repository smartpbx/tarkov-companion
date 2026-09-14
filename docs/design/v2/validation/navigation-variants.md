# Navigation variants

> **Status: not yet run.** Neither variant has been tested. Neither is preferred by this document.

Two navigation models, built as storyboards over **identical sample content**
([prototype/content.js](prototype/content.js)) and the **same task cards**. The only intended
differences are the destinations, their labels, and where results and Intel open. If a session
shows a difference caused by anything else, that is a storyboard defect: log it and fix it.

## What is identical in both

- Every sample name, number, time, confidence value and model figure.
- The header on every page: profile context, local time as one label, raid state with elapsed time as another, a counted setup status link, and a **Capture** button that shows what is armed.
- The Capture dialog, the nine capture intents (#264 `ScanIntent`), the stage progress, and every mismatch, unknown,
  still-writing, duplicate and device-race flow ([capture-intent-mismatch.md](capture-intent-mismatch.md)).
- Raid, Loot decision, Stash scan, Team, the tablet preview, and the Debrief or History content.
- Every state from [state-matrix.md](state-matrix.md), a Map and List toggle for every map, and
  the same focus, announcement and keyboard behaviour.
- The keyboard shortcut Alt+Shift+C for Capture, which can be turned off.

## Variant A: workspace rail

A persistent rail of five workspaces, a separately labelled **Setup & Admin** destination under its
own heading, and **Capture** in the header on every page.

```text
Header: profile · local time · raid state/elapsed · "1 setup item needs action" · [Capture]
Rail:   Raid | Intel | Plan | Team | Debrief
        ── Setup ──
        Setup & Admin
```

| Destination | Contains | First launch |
| --- | --- | --- |
| **Raid** | Map or list, raid state, routes with modelled-traffic provenance, latest capture. **Loot decision** result at `#/raid/loot` | |
| **Intel** | Search and results, item details at `#/intel/item/<id>`, **Stash scan** at `#/intel/stash` | |
| **Plan** | Suggested bundles, objectives, extract, requirements with sources, route estimate | |
| **Team** | Members and roles, paired devices, marks, tablet preview at `#/tablet` | |
| **Debrief** | Raid list, timeline with how each fact is known, corrections, preserved prediction | |
| **Setup & Admin** | Get ready checklist, ten sections: Game and profile, Recognition, Data, Team and devices, Updates, Privacy, Appearance, Accessibility and Diagnostics; links to full Safety and data methodology | **Lands here** |

Intel from elsewhere: a "Details" link switches to the Intel workspace (the rail highlights Intel)
and offers "Back to" the page it came from.

Global search: none. Search lives in Intel.

## Variant B: workflow hub

A persistent row of five destinations organised by when you use them, **Search** and **Capture** in
the header on every page, **Setup** as a header link, and **Intel as a panel** that opens beside the
current page with its own address.

```text
Header: profile · local time · raid state/elapsed · "1 setup item needs action" · Setup · [Search] · [Capture]
Nav:    Home | Raid | Prepare | Team | History
Intel:  opens beside the current page at  #/<current page>/intel/<item>
```

| Destination | Contains | First launch |
| --- | --- | --- |
| **Home** | Get ready checklist, Continue (current plan, last capture, last raid), privacy at a glance | **Lands here** |
| **Raid** | Same as A. **Loot decision** result at `#/raid/loot` | |
| **Prepare** | Same content as A's Plan, plus **Stash scan** at `#/prepare/stash` | |
| **Team** | Same as A | |
| **History** | Same content as A's Debrief | |
| **Setup** (header link) | Same as A's Setup & Admin, including full Safety and data-methodology links | |
| **Search** (header) | Results at `#/search`, each opening Intel beside the results | |

Intel from elsewhere: "Details" opens the Intel panel beside the page, moves focus to the panel's
heading, and "Close Intel" returns focus to the link that opened it. The address, for example
`#/raid/loot/intel/electric-drill`, can be copied, reopened, or sent to a paired tablet.

## Where each v1 surface goes

From the #256 information-architecture table.

| v1 surface | #256 workspace | Variant A | Variant B |
| --- | --- | --- | --- |
| Raid, Map | Raid | Raid | Raid |
| Scanner | Raid | Capture in header; results in Raid (loot) or Intel (stash) | Capture in header; results in Raid (loot) or Prepare (stash) |
| Items, Ammo, Keys, Flea | Intel | Intel | Search, then Intel beside the current page |
| Quests, Hideout, Loadout, Events | Plan | Plan | Prepare |
| Squad, Group | Team | Team | Team |
| History | Debrief | Debrief | History |
| Settings | Setup & Admin | Setup & Admin (rail) | Setup (header), readiness on Home |
| *(none in v1)* | first-run home (#256 §5) | Setup & Admin, Get ready | Home |
| *(none in v1)* | paired tablet | Team, Open tablet preview | Team, Open tablet preview |

## Tablet mapping

The tablet preview shows the same variant's destination names as large buttons, the three modes,
the navigation-owner line, marks placed by choosing a named place, and capture arming. In **Follow
desktop** the destination buttons are present but marked unavailable, with a note saying to change
mode. **Show this view on desktop** appears only in **Independent view**.

## Storyboard addresses

| Journey start | Variant A | Variant B |
| --- | --- | --- |
| J1 first launch | `variant-a.html#/setup` | `variant-b.html#/home` |
| J2 loot decision | `variant-a.html#/raid` | `variant-b.html#/raid` |
| J3 stash scan | `variant-a.html#/raid` (then find Stash scan) | `variant-b.html#/raid` (then find Stash scan) |
| J4 next-raid plan | `variant-a.html#/plan` | `variant-b.html#/prepare` |
| J5 paired tablet | `variant-a.html#/tablet` | `variant-b.html#/tablet` |
| J6 debrief | `variant-a.html#/debrief` | `variant-b.html#/history` |

The moderator panel links to each of these and resets the session.

## Findability tasks

Known asymmetry: Variant A's Intel page lists recent and planned items with prices before any search, while Variant B shows results only after a header search. Record F-02 first clicks with that in mind.

The same twelve task cards are used in both variants. Each participant does **set 1** on one
variant and **set 2** on the other (research-plan.md §4). Start every task from the Raid page with
the session reset. Read the card; do not name a destination.

**Correct** means the participant reaches a listed destination and activates the listed control.
Any listed alternative is correct. Record the first click even when the end is correct.

### Set 1

| ID | Task card | Correct in A | Correct in B | Hypotheses |
| --- | --- | --- | --- | --- |
| F-01 | "You have just installed the companion. Check whether everything it needs is working." | Setup & Admin, Get ready; or the header setup link | Home, Get ready; or the header setup link | H-01, H-15 (starts from Raid, so descriptive only for H-03) |
| F-03 | "Change which folder the companion watches for screenshots." | Setup & Admin, Screenshot folder, Change folder | Header Setup or Home, Screenshot folder, Change folder | H-01, H-03 |
| F-05 | "Tell the companion that your next screenshot is for a loot decision." | Header Capture, Loot decision, Arm | Same | H-04 |
| F-07 | "See whether your teammate Moth is up to date." | Team, Members | Same | H-01 |
| F-09 | "Check whether you have everything for your next Customs run." | Plan, Requirements | Prepare, Requirements; or Home, Continue, plan link | H-01, H-07 |
| F-11 | "You restarted the companion. Pick up where you left off." | Raid; or Plan; or Debrief (A has no Home: record which one, and what the participant expected to see) | Home, Continue | H-03, descriptive only: every A destination is correct, so F-11 cannot move a placement rule (metrics-and-analysis.md) |

### Set 2

| ID | Task card | Correct in A | Correct in B | Hypotheses |
| --- | --- | --- | --- | --- |
| F-02 | "Find out what a Virtex processor is worth." | Intel, search, Virtex processor | Header Search, Virtex processor (Intel panel) | H-01, H-05 |
| F-04 | "Find out whether the traffic shown on the Raid map is live." | Raid, Routes panel, model label and provenance | Same | H-10 |
| F-06 | "Start scanning your whole stash." | Intel, Open stash scan; or Capture, Full stash | Prepare, Open stash scan; or Capture, Full stash | H-06, H-04 |
| F-08 | "Look at what happened in your last raid." | Debrief | History; or Home, Continue, last raid | H-07 |
| F-10 | "Let your tablet change what the desktop companion shows." | Team, Open tablet preview, Control desktop | Same | H-11 |
| F-12 | "You are on a loot decision. Check the flea fee for the electric drill without losing the loot decision." Start at `#/raid/loot`. | Details for Electric drill, then Back to Loot decision | Details for Electric drill (panel), then Close Intel | H-05 |
