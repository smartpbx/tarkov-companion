# V2 native accessibility evidence

This directory records the implementation contract and deterministic fixtures for #266. It does
not claim a Narrator, NVDA, screen-magnifier, Windows contrast, RTL, or human touch session; those
need packaged Windows evidence in #279, while real-participant navigation decisions remain open in
#265.

The CI-safe gallery is synthetic and resource-backed. Its tree and structural visual matrix are
checked at 100%, 125%, 150%, and 200% text and at narrow effective widths. It has no current clock,
network result, animation frame, host font fallback, real player data, or generated screenshot.

See [contracts.md](contracts.md) for implementation conventions and [baselines.md](baselines.md) for
what each fixture is and is not evidence of.
