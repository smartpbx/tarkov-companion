# Third-party notices

This document records the third-party components resolved for the `net10.0` source and test projects and the components present in the self-contained `win-x64` archive. The exact per-package graph, NuGet SHA-512 content hashes, project reachability, runtime/build/test classification, license mapping, and bundled-component records are in `THIRD_PARTY_INVENTORY.json`. License and upstream notice texts named below ship in `LICENSES/`.

## Shipped runtime components

<!-- notice:dotnet-runtime -->
### .NET runtime

- Component/version: Microsoft .NET self-contained `win-x64` runtime 10.0.12.
- Copyright/notice: Copyright (c) .NET Foundation and Contributors; Microsoft and third-party attributions are retained verbatim in the runtime package's notice file.
- License: MIT, with additional licenses for incorporated material enumerated by the upstream notice.
- Purpose: managed runtime and native Windows runtime host used by the self-contained application and simulator.
- Authoritative source: [`Microsoft.NETCore.App.Runtime.win-x64` 10.0.12](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/10.0.12), whose package metadata identifies dotnet/dotnet commit `95017c711e6afc1085133d440e42b4bd78155701`.
- Ships: yes. See `LICENSES/Dotnet-Runtime-MIT.txt` and `LICENSES/Dotnet-Runtime-THIRD-PARTY-NOTICES.txt`, copied from that locked runtime package.

<!-- notice:avalonia -->
### Avalonia

- Component/version: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Fonts.Inter`, `Avalonia.FreeDesktop`, `Avalonia.FreeDesktop.AtSpi`, `Avalonia.HarfBuzz`, `Avalonia.Native`, `Avalonia.Remote.Protocol`, `Avalonia.Skia`, `Avalonia.Themes.Fluent`, `Avalonia.Win32`, and `Avalonia.X11`, all 12.1.2.
- Copyright/notice: Copyright 2013-2026 © The AvaloniaUI Project. Avalonia's retained upstream notice contains the copyrights and license terms for incorporated WPF, Silverlight Toolkit, wayland-protocols, Metsys.Bson, RichTextKit, Mono, Collections.Pooled, EllipticalArc.java, WinUI, Chromium, Flutter, and Reactive Extensions material.
- License: MIT for Avalonia; incorporated-material terms are reproduced verbatim in Avalonia's notice.
- Purpose: application/simulator UI, themes, rendering integration, accessibility, and platform backends.
- Authoritative source: [Avalonia commit `d3c867a9e2de379249b03dbeb3495bd7f076a81a`](https://github.com/AvaloniaUI/Avalonia/tree/d3c867a9e2de379249b03dbeb3495bd7f076a81a), recorded by every locked 12.1.2 package.
- Ships: yes. See `LICENSES/Avalonia-MIT.txt` and `LICENSES/Avalonia-THIRD-PARTY-NOTICES.md`.

<!-- notice:angle -->
### ANGLE

- Component/version: `Avalonia.Angle.Windows.Natives` 2.1.27548.20260419, containing `av_libglesv2.dll` for win-x64.
- Copyright/notice: Copyright 2018 The ANGLE Project Authors. All rights reserved.
- License: BSD-3-Clause.
- Purpose: OpenGL ES translation used by Avalonia's Windows renderer.
- Authoritative source: the [locked NuGet package](https://www.nuget.org/packages/Avalonia.Angle.Windows.Natives/2.1.27548.20260419) and its repository commit [`1c89805903c1482166356d3b950d474973180e61`](https://github.com/AvaloniaUI/angle/tree/1c89805903c1482166356d3b950d474973180e61).
- Ships: yes. See `LICENSES/ANGLE-BSD-3-Clause.txt`, copied from the locked package.

<!-- notice:inter-font -->
### Inter typeface

- Component/version: Inter 3.019, font metadata revision `git-0a5106e0b`, embedded in `Avalonia.Fonts.Inter.dll` 12.1.2.
- Copyright/notice: Copyright (c) 2016-2020 The Inter Project Authors. “Inter” is a trademark of Rasmus Andersson.
- License: SIL Open Font License 1.1 (`OFL-1.1`).
- Purpose: default application typeface.
- Authoritative source: the embedded font name table and [Inter revision `0a5106e0b`](https://github.com/rsms/inter/tree/0a5106e0b).
- Ships: yes. See `LICENSES/Inter-OFL-1.1.txt`.

<!-- notice:communitytoolkit -->
### .NET Community Toolkit

- Component/version: `CommunityToolkit.Mvvm` 8.4.2.
- Copyright/notice: Copyright © .NET Foundation and Contributors. All rights reserved.
- License: MIT.
- Purpose: observable and command infrastructure for MVVM presentation.
- Authoritative source: [commit `35dfe7b12da0e64384bfaec7ebc0883196a89f9f`](https://github.com/CommunityToolkit/dotnet/tree/35dfe7b12da0e64384bfaec7ebc0883196a89f9f), recorded by the locked package.
- Ships: yes. See `LICENSES/CommunityToolkit-MIT.md`, copied from the locked package.

<!-- notice:microsoft-dotnet -->
### Microsoft managed libraries

- Component/version: `Microsoft.Data.Sqlite` and `Microsoft.Data.Sqlite.Core` 10.0.12; `Microsoft.Extensions.Configuration`, `.Configuration.Abstractions`, `.Configuration.Binder`, `.Configuration.FileExtensions`, `.Configuration.Json`, `.DependencyInjection`, `.DependencyInjection.Abstractions`, `.Diagnostics`, `.Diagnostics.Abstractions`, `.FileProviders.Abstractions`, `.FileProviders.Physical`, `.FileSystemGlobbing`, `.Http`, `.Logging`, `.Logging.Abstractions`, `.Options`, `.Options.ConfigurationExtensions`, and `.Primitives`, all 10.0.12.
- Copyright/notice: © Microsoft Corporation. All rights reserved.
- License: MIT.
- Purpose: managed SQLite access plus configuration, dependency injection, diagnostics, HTTP, logging, options, and file-provider infrastructure.
- Authoritative source: [dotnet/dotnet commit `95017c711e6afc1085133d440e42b4bd78155701`](https://github.com/dotnet/dotnet/tree/95017c711e6afc1085133d440e42b4bd78155701), recorded by each locked 10.0.12 package.
- Ships: yes. See `LICENSES/Dotnet-Runtime-MIT.txt`.

<!-- notice:sqlite -->
### SQLitePCLRaw and SQLite

- Component/version: `SQLitePCLRaw.bundle_e_sqlite3`, `.core`, `.lib.e_sqlite3`, and `.provider.e_sqlite3` 2.1.12; the packaged `e_sqlite3.dll` identifies SQLite 3.53.3 with source id `d4c0e51e4aeb96955b99185ab9cde75c339e2c29c3f3f12428d364a10d782c62`.
- Copyright/notice: SQLitePCLRaw copyright 2014-2024 SourceGear, LLC. The SQLite authors disclaim copyright and publish the blessing reproduced in the upstream SQLitePCLRaw notice.
- License: Apache-2.0 for SQLitePCLRaw; SQLite is public domain (`LicenseRef-SQLite-Public-Domain`).
- Purpose: managed/native local database provider and engine.
- Authoritative source: [SQLitePCLRaw tag `v2.1.12`](https://github.com/ericsink/SQLitePCL.raw/tree/v2.1.12), the package's Apache-2.0 metadata, and the native library's embedded SQLite source id.
- Ships: yes. See `LICENSES/Apache-2.0.txt` and the verbatim `LICENSES/SQLitePCLRaw-NOTICE.txt`. The locked win-x64 native DLL SHA-256 is `b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e`.

<!-- notice:microcom -->
### MicroCom.Runtime

- Component/version: `MicroCom.Runtime` 0.11.6.
- Copyright/notice: Copyright (c) 2021 Nikita Tsukanov.
- License: MIT.
- Purpose: COM interop runtime resolved by Avalonia.
- Authoritative source: [commit `76785efcafd91b5902fd19dd11145f6dd655b7b4`](https://github.com/kekekeks/MicroCom/tree/76785efcafd91b5902fd19dd11145f6dd655b7b4), recorded by the locked package.
- Ships: yes. See `LICENSES/MicroCom-MIT.txt`.

<!-- notice:tmds-dbus -->
### Tmds.DBus.Protocol

- Component/version: `Tmds.DBus.Protocol` 0.94.1.
- Copyright/notice: Copyright 2006 Alp Toker; Copyright 2010 Other Contributors; Copyright 2016 Tom Deseyn.
- License: MIT.
- Purpose: D-Bus protocol support resolved by Avalonia Desktop; the managed assembly remains in the untrimmed Windows archive.
- Authoritative source: [commit `b4a7fed0b878f74cb54f7cca84d2889af4e596ba`](https://github.com/tmds/Tmds.DBus/tree/b4a7fed0b878f74cb54f7cca84d2889af4e596ba), recorded by the locked package.
- Ships: yes. See `LICENSES/Tmds-DBus-MIT.txt`.

<!-- notice:skia -->
### SkiaSharp and native Skia

- Component/version: `SkiaSharp` and `SkiaSharp.NativeAssets.Win32` 3.119.4, including `libSkiaSharp.dll`.
- Copyright/notice: SkiaSharp copyright (c) 2015-2016 Xamarin, Inc. and copyright (c) 2017-2018 Microsoft Corporation. Native Skia copyright (c) 2011 Google Inc.; all bundled third-party copyrights and terms are retained in the package notice.
- License: MIT for the managed/native wrapper distribution; BSD-3-Clause for Skia; additional incorporated-material terms are enumerated verbatim by the package.
- Purpose: 2D rendering for Avalonia and SVG map preview rasterization.
- Authoritative source: [SkiaSharp commit `f568ac94dd768ef9a2f593537cfde2dd0d348ef5`](https://github.com/mono/SkiaSharp/tree/f568ac94dd768ef9a2f593537cfde2dd0d348ef5) recorded by the locked packages.
- Ships: yes. See the package-exact `LICENSES/SkiaSharp-HarfBuzzSharp-MIT.txt` and `LICENSES/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt`.

<!-- notice:harfbuzz -->
### HarfBuzzSharp and native HarfBuzz

- Component/version: `HarfBuzzSharp` and `HarfBuzzSharp.NativeAssets.Win32` 8.3.1.3, containing HarfBuzz 8.3.1 in `libHarfBuzzSharp.dll`.
- Copyright/notice: HarfBuzzSharp copyright (c) 2015-2016 Xamarin, Inc. and copyright (c) 2017-2018 Microsoft Corporation. The native HarfBuzz notice retains the named Google, Mozilla, Codethink, Nokia, Red Hat, and individual contributor copyrights.
- License: MIT for HarfBuzzSharp; HarfBuzz's MIT-style “Old MIT” terms are reproduced in the package's complete notice.
- Purpose: OpenType text shaping.
- Authoritative source: the [locked Windows native-assets package](https://www.nuget.org/packages/HarfBuzzSharp.NativeAssets.Win32/8.3.1.3).
- Ships: yes. See `LICENSES/SkiaSharp-HarfBuzzSharp-MIT.txt` and `LICENSES/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt`, both copied from the locked package.

<!-- notice:svg-stack -->
### Svg.Skia rendering stack

- Component/version: `Svg.Skia`, `Svg.Animation`, `Svg.Model`, `Svg.SceneGraph`, and `ShimSkiaSharp` 4.9.1 (MIT); `Svg.Custom` 4.9.1 (MS-PL); `ExCSS` 4.3.1 (MIT).
- Copyright/notice: Svg.Skia packages copyright © Wiesław Šoltés 2026; their repository license copyright is (c) 2020 Wiesław Šoltés. ExCSS copyright (c) 2024 Tyler Brinks.
- License: MIT for the named Svg.Skia packages and ExCSS; Microsoft Public License (`MS-PL`) for `Svg.Custom`.
- Purpose: parsing, validating, modeling, and rasterizing runtime-cached SVG map artwork.
- Authoritative source: [Svg.Skia commit `8b23ee1e01b1f2ba90eb74b71ab8c7f27d49f037`](https://github.com/wieslawsoltes/Svg.Skia/tree/8b23ee1e01b1f2ba90eb74b71ab8c7f27d49f037), including its [`Svg.Custom` license](https://github.com/wieslawsoltes/Svg.Skia/blob/8b23ee1e01b1f2ba90eb74b71ab8c7f27d49f037/src/Svg.Custom/LICENSE.TXT), and [ExCSS commit `c97e84d6126bb2e42658cf5af627d52e697c8779`](https://github.com/TylerBrinks/ExCSS/tree/c97e84d6126bb2e42658cf5af627d52e697c8779).
- Ships: yes. See `LICENSES/SvgSkia-MIT.txt`, `LICENSES/SvgCustom-MS-PL.txt`, and `LICENSES/ExCSS-MIT.txt`.

<!-- notice:ocr -->
<!-- notice:ocr-model -->
### TesseractOCR, native Tesseract, and English model

- Component/version: `TesseractOCR` 5.5.2 managed wrapper; Tesseract 5.5.1 x86/x64 native libraries; `tessdata_fast` `eng.traineddata` from commit `87416418657359cb625c412a48b6e1d6d41c29bd`.
- Copyright/notice: wrapper copyright 2012-2021 Charles Weld and 2021-2023 Kees van Spelde; Tesseract/Tessdata contributors retain their upstream copyrights.
- License: Apache-2.0 for wrapper, native Tesseract, and trained data.
- Purpose: offline OCR and its embedded English LSTM model.
- Authoritative source: [wrapper/package commit `787ebdb488184f47df3dcad7fe687b0b95d0d98c`](https://github.com/Sicos1977/TesseractOCR/tree/787ebdb488184f47df3dcad7fe687b0b95d0d98c), [Tesseract 5.5.1](https://github.com/tesseract-ocr/tesseract/tree/5.5.1), and [`tessdata_fast` commit `87416418657359cb625c412a48b6e1d6d41c29bd`](https://github.com/tesseract-ocr/tessdata_fast/tree/87416418657359cb625c412a48b6e1d6d41c29bd).
- Ships: yes. See `LICENSES/Apache-2.0.txt` and the package-exact `LICENSES/TesseractOCR-README.md`. Tesseract x64/x86 SHA-256 values are `e9903623e901f2d754eb49eea4dbff229f599a8817055a3e3f1c292216670ca9` and `5fb5b163e9b78e584a8d320df7a75d3713ba6c925ba274ab35ba0cce15567ff8`; the embedded model SHA-256 is `7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2`.

<!-- notice:ocr-codecs -->
### Leptonica and statically bundled image codecs

- Component/version: Leptonica 1.85.0, with identifiable compiled-in libjpeg-turbo 2.0.6, libpng 1.6.37, libtiff 4.3.0, and zlib 1.2.11 code in the packaged x86/x64 `leptonica-1.85.0.dll` files.
- Copyright/notice: Leptonica copyright (C) 2001-2020 Leptonica; the complete copyright statements for the Independent JPEG Group/libjpeg-turbo contributors, PNG Reference Library authors, Sam Leffler and Silicon Graphics, and Jean-loup Gailly and Mark Adler are retained in their verbatim upstream files.
- License: BSD-2-Clause for Leptonica; IJG AND BSD-3-Clause AND Zlib for libjpeg-turbo; Libpng for libpng; libtiff license for libtiff; Zlib for zlib.
- Purpose: OCR image loading and compression support. Versions were verified from identical version strings in both locked architecture binaries; these libraries are statically present because no corresponding codec DLL imports exist.
- Authoritative source: [Leptonica 1.85.0](https://github.com/DanBloomberg/leptonica/tree/1.85.0), [libjpeg-turbo 2.0.6](https://github.com/libjpeg-turbo/libjpeg-turbo/tree/2.0.6), [libpng 1.6.37](https://github.com/pnggroup/libpng/tree/v1.6.37), [libtiff 4.3.0](https://github.com/libsdl-org/libtiff/tree/v4.3.0), and [zlib 1.2.11](https://github.com/madler/zlib/tree/v1.2.11).
- Ships: yes. See `LICENSES/Leptonica-BSD-2-Clause.txt`, `LICENSES/libjpeg-turbo-LICENSE.md`, `LICENSES/libjpeg-turbo-README.ijg`, `LICENSES/libpng-LICENSE.txt`, `LICENSES/libtiff-COPYRIGHT.txt`, and `LICENSES/zlib-README.txt`. Leptonica x64/x86 SHA-256 values are `64bb1d584bce83db5e5c3625f1857e4bc12993625699cabd0a9b8b76e393bd01` and `424162e2a630c3591d451f98bf73e9bf1caaed3dcd2e198ceb053f4ef8f41f33`.

The TesseractOCR package does not include a separate NOTICE or native-dependency manifest. Its package metadata, packaged README, repository commit, PE imports, and embedded native version/copyright strings are therefore the authoritative evidence available for this binary payload. The native DLLs require the Microsoft Visual C++ 2015-2022 runtime, which is a target-machine prerequisite and is not included in the archive.

## Resolved but not shipped

<!-- notice:build-test -->
- Build-only: `Avalonia.BuildServices` 11.3.2 (MIT). It participates in compilation and is absent from the release archive.
- Other-platform assets: `HarfBuzzSharp.NativeAssets.Linux`, `.macOS`, and `.WebAssembly` 8.3.1.3, plus `SkiaSharp.NativeAssets.Linux`, `.macOS`, and `.WebAssembly` 3.119.4 (MIT package metadata). They appear in the cross-platform resolved graph but the win-x64 publish selects only the Windows payloads.
- Test-only: `Microsoft.NET.Test.Sdk`, `Microsoft.TestPlatform.ObjectModel`, `Microsoft.TestPlatform.TestHost`, and `Microsoft.CodeCoverage` 18.10.0 (MIT); `coverlet.collector` 10.0.1 (MIT); `xunit` 2.9.3, `xunit.abstractions` 2.0.3, `xunit.analyzers` 1.18.0, `xunit.assert` 2.9.3, `xunit.core` 2.9.3, `xunit.extensibility.core` 2.9.3, `xunit.extensibility.execution` 2.9.3, and `xunit.runner.visualstudio` 4.0.0 (Apache-2.0). None ships in the Windows archive.

Exact package URLs, content hashes, dependency relationships, and project reachability for these entries are retained in `THIRD_PARTY_INVENTORY.json`.

## Data services and runtime-cached artwork

- tarkov.dev / the-hideout contributors provide structured Escape from Tarkov data through `json.tarkov.dev`; see <https://tarkov.dev> and <https://github.com/the-hideout/tarkov-api>.
- `the-hideout/tarkov-dev` contributors provide runtime map configuration from `src/data/maps.json` under MIT terms; see <https://github.com/the-hideout/tarkov-dev>.
- `the-hideout/tarkov-dev-svg-maps` contributors and the author credited by each selected variant provide optional runtime-cached map artwork under CC BY-NC-SA 4.0; see <https://github.com/the-hideout/tarkov-dev-svg-maps> and <https://creativecommons.org/licenses/by-nc-sa/4.0/>. The upstream repository additionally prohibits use in cheating or unfair-advantage software, including radar/ESP overlays, cheat-client maps, automation, and pixel bots.

Third-party map artwork is not included in the source tree or release archive. Original URLs, author links, retrieval timestamps, hashes, and license references remain in the user's local cache metadata and are attributed in-app.
