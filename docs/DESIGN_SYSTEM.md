# Design system

## Direction

The app is a cartographic instrument panel for a second monitor: map-first, information-dense, calm at rest, and emphatic only when an item or route needs immediate attention. It does not imitate Escape from Tarkov's visual chrome.

## Tokens

- Obsidian canvas `#11151B`
- Gunmetal surface `#1A212A`
- Cold steel text `#C6D0D8`
- Survey cyan `#56B8C6` for position/navigation
- Field ochre `#C6A15B` for economy/value
- Signal coral `#DF6A62` for risk/allergy
- Triage sage `#77B895` for confirmed safe/keep states

Inter is used deliberately for high legibility at monitor distance, with tabular figures for prices and time. Labels use sentence case; ratings always include text and never rely on color alone.

## Layout

```text
┌ status: evidence and freshness, never decoration ┐
├ compact rail ┬ map / primary workspace ┬ context ┤
│ navigation   │ wide, pan/zoom/layers    │ action  │
│              │                          │ reasons │
├──────────────┴ last observed scan / uncertainty ┤
```

The wide map/workspace anchors the eye. The context rail changes by selection rather than multiplying identical cards. Borders encode grouping and selected state; corner radii remain restrained. Motion is limited to evidence updates and user-triggered panel transitions.
