# V2 native accessibility evidence

This directory records the implementation contract for #266 and what its automated checks do and do
not prove. It does not claim a Narrator, NVDA, screen-magnifier, Windows contrast, RTL, colour-vision,
or human touch session. Those need packaged Windows evidence in #279, and real-participant
navigation decisions remain open in #265.

The gallery (`Views/V2/Primitives/V2PrimitiveGallery.axaml`) is synthetic and resource-backed. CI
reads its source as XML and loads the compiled V2 dictionaries, but nothing instantiates, lays out,
renders, or captures the gallery at any text scale or width. It has no current clock, network result,
animation frame, host font fallback, or real player data.

- [contracts.md](contracts.md): implementation conventions, and the Avalonia 12.1.2 automation facts
  they rest on.
- [operational-semantics.md](operational-semantics.md): cross-platform credibility, capture, and
  paired-device axes, their progressive-disclosure levels, and the feature-ownership boundary.
- [render-matrix.md](render-matrix.md): the scale, width, and variant cases the gallery still has to
  be rendered at, and what would count as evidence.
