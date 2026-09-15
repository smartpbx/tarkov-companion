# Render matrix

[`Assets/V2/render-matrix.v1.json`](../../../../src/TarkovCompanion.App/Assets/V2/render-matrix.v1.json)
lists the cases the native primitive gallery must be rendered, inspected, and approved at:

- 100, 125, 150, and 200% text;
- expanded, standard, compact, and narrow effective widths, including a narrow tablet case;
- dark, light, high-contrast, red-green-safe, blue-yellow-safe, and monochrome variants;
- the elements each case must visibly contain;
- the tree each case must expose.

It was first committed as `visual-baselines.v1.json` and described as checked at every scale. That
was wrong: no test renders the gallery, so nothing had been checked. The file is now marked
`"status": "unrendered"` with `#279` as its evidence owner. A contract test pins both, and checks
that the matrix still covers every manifest text scale, a narrow width, and each colour-vision
variant.

What CI does verify is listed in [`docs/DESIGN_SYSTEM.md`](../../../DESIGN_SYSTEM.md#evidence-boundary):
the gallery's declared structure and the compiled resources, not pixels or a UIA tree.

When #279 captures packaged Windows, each case needs:

- expected, actual, and diff artifacts;
- runner-noise measurements;
- a UIA tree dump;
- human approval.

Baselines are never auto-approved, and replacing or editing this checklist does not satisfy that
gate. Stable cross-platform raster capture would not be evidence of native Windows UIA or of
assistive-technology behaviour either.
