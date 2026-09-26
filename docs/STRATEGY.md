# Strategy predictions

Every traffic, risk, and rotation result carries this disclaimer:

> Predicted traffic based on map and game knowledge—not live player data.

The strategy layer consumes only static authored zone weights, the elapsed raid fraction, and the user's last-known screenshot position. It has no enemy objects, live player inputs, renderer hooks, game memory, or network observations.

## Traffic field and risk panel

Raid time is split into equal early, mid, and late thirds. Spawn influence decays with the square of remaining time, while extract attraction rises with the square of elapsed time. POI influence peaks mid-raid, chokepoints receive a smaller mid-raid lift, and quest influence tapers gradually. Inputs are bounded before being combined.

The risk panel sorts the same calculated samples into Low, Moderate, and High bands. “Current area” is an estimate derived from distance to static zone centers and the optional last-known player position. Without that position, current-area risk is Unknown.

Rotation flows connect strong spawn zones to nearby objectives early and objective zones toward extracts later. They are generic planning narratives, not detected paths. The code-authored `assets/strategy/generic-training-ground.json` exists only as a distributable fixture.

## The Raid map's modelled traffic (V2)

The Raid page draws a heat layer out of the box (`MapPriorTrafficModel`, version `map-prior-1`). Its only inputs are the catalog's player and boss spawns, extracts, place names and #318's high-value loot spawns, straight lines between them, and the player's own past trails on the map (at most 35%, at 14 raids). The phase weighting is the strategy model above. It is labelled "Prior from map structure, not recorded raids" with its counts, catalog date, generated time and a confidence that never reaches 50%. The governed snapshot store (`Traffic/Inbox`) is for models of recorded raids; the project has none, and a snapshot names regions without placing them, so it fills the plan card's rows when installed and cannot draw a field.

Routes on that map are the planner below (`TrafficRoutePlanner` gives it the field as a grid graph: AvoidPvP for the suggested route, Fastest for the direct line). The graph is flagged incomplete because the catalog has no walls or water, so the page calls it straight-line guidance; times are path length plus a quarter, at 1.8 to 3.2 m/s; every "Why this route" line is read from the two routes' own numbers.

`PersonalPace` (#712 2-4) measures the player's own pace from their recorded trails (moving legs between their screenshots, timed by the in-game clock in the names) and falls back to 1.8 to 3.2 m/s below 8 legs from 2 raids; Debrief shows it, with the exit they use most and their last five raids on the map. `PersonalExits.Choose` is the exit weighting for the Now panel: a used exit counts up to a quarter nearer, and an offered exit still wins.

## Route planner

The planner uses phase-valid directed edges in a static navigation graph:

- Fastest minimizes travel cost.
- Safest weights known risk three times.
- Quest favors edges arriving at quest nodes.
- Loot rewards declared loot utility while retaining travel and risk cost.
- AvoidPvP weights known risk five times.

A complete graph route is reported with 0.80 confidence. A route across a graph flagged incomplete is explicitly imprecise with 0.45 confidence. If known edges cannot connect the requested points, the planner returns only the start and says that no route was guessed; users must fall back to the map and in-game judgment.
