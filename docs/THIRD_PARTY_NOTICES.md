# Third-party notices

This file is completed from the locked dependency graph before release.

## Data services

- tarkov.dev / the-hideout contributors — structured Escape from Tarkov data from `json.tarkov.dev`. See <https://tarkov.dev> and <https://github.com/the-hideout/tarkov-api>.
- `the-hideout/tarkov-dev` contributors — runtime map configuration from `src/data/maps.json`, MIT licensed. See <https://github.com/the-hideout/tarkov-dev>.

## Optional runtime map artwork

- `the-hideout/tarkov-dev-svg-maps` contributors and the author credited by each selected variant — runtime-cached map artwork, CC BY-NC-SA 4.0. See <https://github.com/the-hideout/tarkov-dev-svg-maps> and <https://creativecommons.org/licenses/by-nc-sa/4.0/>.
- The upstream map-art repository explicitly prohibits using these assets in software intended to cheat or gain an unfair advantage in Escape from Tarkov, including radar/ESP overlays, cheat-client maps, automation, and pixel bots. Tarkov Companion remains an external read-only companion and does not provide those behaviors.

Map artwork is not included in the source tree or release archive. Original URLs, author links, retrieval timestamps, hashes, and license references remain in the user's runtime cache metadata.

## Frameworks and libraries

- .NET — MIT license, Microsoft and contributors.
- Avalonia — MIT license, Avalonia contributors.
- CommunityToolkit.Mvvm — MIT license, .NET Foundation and contributors.
- Microsoft.Data.Sqlite and Microsoft.Extensions packages — MIT license, Microsoft and contributors.
- xUnit.net — Apache-2.0 license, xUnit.net contributors.
- SkiaSharp — MIT license, Microsoft and contributors.
- Svg.Skia — MIT license, Wiesław Šoltés and contributors.
- TesseractOCR 5.5.2 (including Tesseract 5.5.1 and Leptonica 1.85.0 Windows
  native binaries) — Apache-2.0, Charles Weld, Kees van Spelde, and upstream
  contributors. Source: <https://github.com/Sicos1977/TesseractOCR> at package
  commit `787ebdb488184f47df3dcad7fe687b0b95d0d98c`.
- tessdata_fast English model (`eng.traineddata`) — Apache-2.0, Tesseract OCR
  contributors. Source: <https://github.com/tesseract-ocr/tessdata_fast> at
  commit `87416418657359cb625c412a48b6e1d6d41c29bd`; SHA-256
  `7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2`.

Exact versions, distributed status, and required actions are generated and audited before packaging.
