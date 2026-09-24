# ADR 0021: The next-raid planner

## Status

Accepted (#307). Describes what exists on 2026-09-24; update the tables when a planner changes.

## Context

#307 asked for "the next-raid planning, progression, and event engine". It was built as several
small planners in `Application/Services/Planning` rather than one engine, each a pure function over
data the app already stores, with a thin service that reads that data. Nothing in it calls the
network or the game. This records what the planners read, what they say, and what they deliberately
leave out, so that a suggestion on screen can be traced to a rule and a rule to its limits.

## Decision

1. **Planners are pure.** A planner takes catalog rows, the active profile and recorded quest
   progress as arguments and returns records. Services read; planners decide. Tests feed planners
   seed-catalog shapes directly.
2. **Every output carries its reason.** A route leg says why it is next, a hideout step says what
   gates it, a quest state says why it is blocked, a loadout suggestion names the objective and the
   quest it serves. A rule that cannot say why does not say anything.
3. **Unknown is not zero.** An item no scan has counted reads "not scanned", never "0" (#509).
4. **Only stated conditions.** A suggestion comes from a condition json.tarkov.dev states on an
   objective, an offer or a station level. Nothing is inferred from what players usually do.
5. **Outputs are version-stamped.** `PlannerVersions` holds one rules version per planner that
   shows output on screen (`route-307.1`, `hideout-path-307.1`, `loadout-suggest-307.1`). The
   version is printed as "Rules …" beside the output, the same words a saved loot scan (#784) and
   a stash sort plan (#801) use. Bump it when the same inputs would give a different answer.

## Inputs and outputs

| Planner | Reads | Says |
| --- | --- | --- |
| `QuestStatePlanner` | quest board, trader levels | Current / Next / Future / Blocked / Completed / Unknown, with reasons |
| `QuestUnlockPlanner` | eligibility reasons | what to do to open a locked quest |
| `ObjectiveRoutePlanner` | placed objectives of one map, a start point | visit order, a reason per leg, total metres |
| `HideoutUpgradePlanner` | stations, levels, requirements, prerequisites, owned counts | build order to a target level, time, trader and skill gates, one shopping list |
| `AcquisitionChainPlanner` | cash offers, barters, crafts, loyalty, completed quests | cheapest buy/craft/barter chain, cycle-safe |
| `KeepListPlanner` | quest and hideout needs, key facts, owned counts | what to keep and why |
| `AllergyWarningPlanner` | event records | allergy warnings on planned food and medicine |
| `LoadoutSuggestionPlanner` | active quests' incomplete objectives on one map, item catalog | keys, weapons, suppressors and mods, gear to wear or leave, kill range, items to plant or use, room for pick-ups |

`LoadoutSuggestionService` adds, per suggestion, what the profile owns (stash and case scans, #793)
and the best trader source the profile can use now or what it needs first (#761).

## Modelled and not modelled

Modelled: recorded quest progress and eligibility; catalog objective conditions (keys, weapons,
weapon mods, worn and forbidden gear, distance); straight-line distance between placed objectives;
hideout prerequisites, build time, trader and skill gates; trader loyalty and quest-locked offers;
craft and barter recursion; owned counts from scans; running events and their typed rules.

Not modelled, on purpose:

- **Armour or ammunition "common on a map".** No source this app reads says what enemies wear on a
  map, so there is no "class 4+ armour for Customs". A suggestion that looked like a fact and was a
  guess would be worse than none.
- **Walls, floors, terrain, danger and time.** Routes are straight lines between objectives.
- **Flea market availability and price as a source.** Trader offers only; the flea is shown on Intel.
- **Time-of-day and body-part conditions** on kills. They are in the objective's words, which every
  suggestion shows, but no gear follows from them.
- **Anything live.** No planner reads the game's memory, traffic or screen; none predicts where a
  player is. Predictions are never shown as detections.

## Consequences

- A new planner gets a `PlannerVersions` constant when it first shows output, and a line here.
- A suggestion or route that looks wrong is traced by its "Rules …" label to the rules that made it.
- Loadout suggestions read `QuestObjectiveReadModel.SubtypeJson`, the objective's raw conditions,
  so a new condition json.tarkov.dev adds is ignored until a rule reads it, never misread.
