# Render matrix

[`Assets/V2/render-matrix.v1.json`](../../../../src/TarkovCompanion.App/Assets/V2/render-matrix.v1.json)
lists the cases the native primitive gallery must be rendered, inspected, and approved at:

- 100, 125, 150, and 200% text;
- expanded, standard, compact, and narrow effective widths, including a narrow tablet case;
- resolved full and reduced motion, with state and wording preserved when transitions disappear;
- all nine concrete theme variants: Dark, Light, HighContrast, and the Dark and Light red-green-safe,
  blue-yellow-safe, and monochrome variants;
- the elements each case must visibly contain, each mapped to gallery automation ids;
- the tree each case must expose.

The desktop 125% and tablet narrow cases include the structured credibility, paired-device,
sharing, recovery, capture-progress, correction, and initiating-context examples. The matrix names
desktop/tablet as surfaces for evidence organization only; responsive selection still uses effective
content width, never a device-label breakpoint.

The first matrix named six variants for nine theme dictionaries, and asked for stacked cards, a table
overflow affordance, single-column reflow, and an ordered data alternative that the gallery does not
contain. Those four are now listed under `notInGallery` with `#266` as owner: #279 cannot capture an
element that does not exist.

It was first committed as `visual-baselines.v1.json` and described as checked at every scale. That
was wrong: no test renders the gallery, so nothing had been checked. The file is now marked
`"status": "unrendered"` with `#279` as its evidence owner. A contract test pins both, and checks
that the matrix covers every manifest text scale, a narrow width, and exactly the theme dictionaries
that exist, that every element a case names maps to automation ids present in the gallery, and that
every other token is listed as not in the gallery with an owner.

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
