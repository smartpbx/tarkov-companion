# Deterministic baseline policy

`Assets/V2/visual-baselines.v1.json` is the committed structural baseline for the disconnected
native primitive gallery. Each case names a scale, effective-width class, and required primitive
set; the expected accessibility-tree roles are also committed. The baseline uses labelled synthetic
content and never auto-approves a changed result.

It is intentionally not a bitmap baseline. Stable cross-platform raster capture is not evidence of
native Windows UIA or assistive technology, and the gallery is not yet composed into a product host.
When #279 captures packaged Windows, it must retain expected, actual, and diff artifacts with
runner-noise measurements and human approval; replacing this fixture does not satisfy that gate.
