# Operational presentation semantics

This is the human-readable companion to `Assets/V2/semantic-manifest.v1.json` 1.1. Desktop and
tablet adapters consume the same identities and localized wording keys. They choose native controls
and layout independently; neither adapter receives pixels, protocol transitions, or game-facing
capabilities from this contract.

## Progressive disclosure

| Level | Use for | Required presentation |
| --- | --- | --- |
| Compact | routine provenance, freshness, confidence, and coverage | short word plus glyph; an outline pattern may add a third cue |
| Inline | decision-changing uncertainty, degraded results, recoverable blocks, and current sharing | current fact, consequence, and available recovery beside the affected content |
| Banner | active consent, destructive action, security failure, and unrecoverable failure | named tone, explicit current fact, consequence, and action when one exists |

Potential, Historical, freshness, confidence, coverage, degraded state, and sharing state therefore
have compact identities, but a feature promotes them when the manifest trigger applies. The full
source, observation/data-through UTC, generated UTC, coverage, confidence, model version, and history
remain in the adjacent Why/details disclosure. Historical, modelled, and potential information is
never labelled or announced as live detection.

## Capture mapping

The display grouping preserves the complete Core pipeline while keeping a queue scannable:

| Display stage | Core `CaptureSessionStage` values |
| --- | --- |
| Armed | `Armed`, `AwaitingCapture` |
| Queued | `Settling` |
| Processing | `Decoding`, `DetectingContext`, `DetectingRegions`, `Matching`, `EnrichingProfile`, `Recommending` |
| Needs review | `AwaitingReview` |
| Complete | `Complete` |
| Cancelled | `Cancelled` |
| Failed | `Failed` |

Corrected is a review outcome and is intentionally outside that table. A capture primitive exposes
the intent and human purpose, queue ordinal and total, current stage, initiating context, and only
the actions the feature supplies: correct, retry, or return to context. Rows use the localized
`captureProgressName` template so a translation can reorder ordinal, total, stage, and detail.
The manifest carries every Core intent but marks Flea unavailable for a paired-device request, in
line with the paired protocol; a tablet adapter must not turn that display identity into an action.

The primitive reports user-created screenshots or explicitly selected visible images. It never
captures the screen on its own, reads game memory or traffic, produces game input, or implies an
in-game overlay.

## Paired-device composition

| Axis | Values |
| --- | --- |
| Mode | Follow, Control Pending, Control, Independent |
| Control fact | Desktop authority, desktop approval pending, paired-device lease |
| Connectivity | Connected, Offline, Reconnecting |
| Acknowledgement | Current, Lagging, Conflict |
| Sharing scope | Only me, My paired devices, Team |

These facts combine freely only when the paired-device protocol permits the underlying state. They
are not a second state machine. In particular, the desktop remains canonical authority; Control is
a bounded paired-device lease, Independent is local browsing, and Offline is connectivity rather
than a mode. Reconnect, Retry, Sync, and Resolve conflict are presentation action identities whose
handlers and authorization stay in the paired-device feature.

The localized `pairedDeviceName` template composes all five facts for UIA without English-order
concatenation. Routine delivery changes announce politely; a conflict announces assertively. Full
session, security, and command history belongs in Why/details or Setup/Admin rather than repeated
policy copy on routine screens.

## Evidence boundary

Source parsing and contract tests prove that every semantic value has resource-backed wording and
redundant cues, every original flat state id maps to one structured value, every Core capture phase
and intent is covered, and representative gallery groups have accessible names and actions. They do
not prove rendering, keyboard operation, UIA runtime output, Narrator/NVDA speech, high-contrast
pixels, touch behavior, RTL, or 200% reflow. Those packaged-Windows checks remain #279.
