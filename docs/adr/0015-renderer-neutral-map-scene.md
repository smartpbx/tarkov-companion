# ADR 0015: One renderer-neutral map scene

## Status

Accepted for incremental V2 delivery.

## Context

The desktop map grew around an Avalonia canvas read model. It can already draw a flat map,
stacked floors, markers, regions, routes, names, and group marks, but much of an object's meaning
is implied by a control, colour, or view-model type. A tablet renderer cannot safely reproduce
that state from pixels or from the legacy `MapOverlayElement` alone. In particular, a static
spawn, a last-known position, a user mark, and a historical estimate must not collapse into the
same generic point.

V2 also needs a fast 2D default, an optional floor stack, and reviewed 3D interiors without
building three contradictory map models. Layer visibility, floor selection, camera state, stable
object identity, hit testing, and the non-spatial list must survive a desktop/tablet handoff.

## Decision

Core owns a renderer-neutral scene snapshot under `Domain/Maps/Scene`.

- The snapshot has one coordinate space, revision, transform version, capability set, camera,
  selected floor, layer state, stable objects, and reviewed assets.
- Point, line, bounded-area, and region geometry use the same objects in flat, floor-stack, and
  interior presentations.
- Object truth is explicit: static reference, potential spawn, local/team last-known state,
  historical estimate, or user-authored mark. Historical estimates cannot be constructed without
  their observation window, generation time, coverage, calibration, transform version, and model
  version.
- A scene asset cannot render until its source, licence, content hash, attribution, map/game
  versions, review status, and review time are present.
- Unsupported presentation requests fall back to flat 2D. Interior mode is advertised only when
  a reviewed interior asset is in the scene; unknown geometry is not generated to fill the gap.
- The visible non-spatial list and renderer-independent hit testing read the same floor and layer
  state as the map.

Application owns adapters into that scene. The first adapter accepts existing overlay elements
only when the caller also supplies that individual element's provenance, and only for semantics it
can preserve exactly: labels, extracts/transits (including faction and offered state), spawn areas,
locks, and quest objectives. Conflicting stable identities withhold the scene instead of choosing
one by input order. Legacy generic marks, routes, and traffic entries are not guessed. Their V2
feature adapters must supply typed scene objects directly.

Avalonia, WebGL/canvas, and future low-cost renderers consume the scene; they do not define it.
The paired-device state stream carries its revisioned view state and user commands rather than a
screenshot of desktop UI state.

## Consequences

- Desktop and tablet can render and mutate one canonical scene without sharing UI-framework
  types.
- Flat 2D remains the required fallback under unsupported GPU, device loss, or absent reviewed
  interiors.
- Feature adapters have more metadata to provide, but the compiler and constructors catch lost
  provenance or ambiguous semantics before a renderer can overstate them.
- Existing map UI can migrate incrementally through the adapter. New V2 layers should target the
  scene contract directly instead of extending `MapOverlayElement`.
- Actual interior-model loading, GPU rendering, device-loss recovery, and tablet transport remain
  later slices of issue #306; this ADR fixes the shared boundary they consume.
