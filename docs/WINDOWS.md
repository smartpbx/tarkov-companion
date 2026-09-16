# Windows observation integrations

`TarkovCompanion.Platform.Windows` implements ordinary, external OS integrations. Every native entry point is behind a Core interface and an `OperatingSystem.IsWindows()` gate, so Linux builds and fixture tests do not load Windows libraries.

## Window discovery and capture

Window discovery enumerates normal processes and their top-level main windows. Production accepts only `EscapeFromTarkov` identities. The companion simulator is returned only when the caller explicitly passes DeveloperMode, and a real EFT window wins when both exist.

Capture uses DPI-aware GDI to copy currently visible pixels from the selected, non-minimized window into an in-memory BGRA buffer. It does not hook a renderer. A virtual-desktop GDI fallback is used only when the request explicitly allows it. Regions must be positive and contained by the target window; captured bytes are never saved by this service.

## Hotkey and displays

The hotkey service owns a small Windows message-loop thread and receives `WM_HOTKEY` after `RegisterHotKey`. It unregisters during disposal. It never calls `SendInput`, `keybd_event`, or any other input-generation API.

Monitor discovery uses ordinary display-monitor enumeration under per-monitor-v2 DPI awareness. Bounds are physical virtual-desktop coordinates, and effective DPI is exposed as a scale relative to 96 DPI.

## Paths and file watchers

Path discovery checks ordinary uninstall registry values and conventional user folders, then returns only directories that exist. Partial discoveries carry reduced confidence rather than fabricated paths. Users can still configure paths manually at the application layer.

The log watcher tails newly appended lines in ordinary `*.log` files with read-sharing enabled and feeds the tolerant application parser. Existing bytes are not replayed on watcher startup. Unknown lines are ignored. The screenshot watcher polls PNG/JPEG metadata, waits for two unchanged probes and a complete image envelope, and emits a path when new bytes have settled. A content change at the same path may therefore be delivered again, while attribute-only changes are ignored. Files older than the two-minute startup grace are not replayed, tracking is bounded to 16,384 paths, and encoded files over 64 MiB or cloud placeholders are held rather than opened. If the configured root disappears or becomes unreadable, the watcher raises a path-free availability signal; the application clears screenshot readiness and rediscovers that source without stopping healthy log observation. Simulator-named paths are ignored unless DeveloperMode was explicit. Both watchers propagate cancellation.

## Secrets

The secret store encrypts UTF-8 values with Windows DPAPI CurrentUser scope and app-specific entropy. Disk filenames are SHA-256 hashes of logical keys, writes are replaced atomically, and plaintext is not written to disk. DPAPI ciphertext is user-and-machine scoped and is not a portable backup format.

## Windows VM validation still required

Linux proves contract behavior and Windows gating but cannot exercise User32, GDI, Shcore, or DPAPI. The Windows VM must verify:

- real and simulator window bounds at 100%, 125%, and mixed-monitor DPI;
- window capture, explicitly allowed desktop fallback, minimized-window behavior, and BGRA orientation;
- hotkey registration, receipt, collision reporting, unregister, and clean shutdown;
- multiple-monitor bounds, primary identity, and scale;
- registry/folder path discovery against an installed or synthetic layout;
- concurrent log writes and settled screenshot creation/replacement, attribute-only changes,
  root deletion/recreation, encoded-size limits, and cloud-placeholder transitions;
- DPAPI set/get/delete and inability to decrypt from another Windows user.

Direct validation against a real EFT installation remains a separate checklist in `LIVE_EFT_VALIDATION.md` and must not be claimed from simulator or VM-only evidence.

## Release and installation

Windows verification builds, launches and packages the desktop and hands the results to its run;
it does not publish them. `publish.yml` takes the artifacts of a successful push-to-main
verification run, refuses unless the portable zip, `BUILD_INFO.txt`, the informational version
compiled into `TarkovCompanion.dll`, and every Velopack file (installer, full and delta packages,
`releases.win.json`, `assets.win.json`, `RELEASES`) agree, then signs each file into a private,
authenticated feed and moves it through canary, beta and stable. [RELEASES.md](RELEASES.md) has
the whole chain.

The installer is unchanged by that: Velopack installs per user under
`%LOCALAPPDATA%\TarkovCompanionDesktop`, and application data stays separately under
`%LOCALAPPDATA%\TarkovCompanion`. The pack id must never equal the data directory name, or
installing renames the player's data aside.

Two things are not true yet, and are recorded rather than implied:

- **In-app updates are not composed yet.** The anonymous public `GithubSource` has been removed,
  so an unconfigured gateway fails closed. The bounded authenticated transport, pinned Sigstore
  verifier and binary/data/model transaction are implemented and fixture-tested, but #294 still
  has to compose them with Velopack and component activation, and #270 supplies persisted state.
- **Delta packages are not produced.** Verification packs full packages only; the release chain
  signs deltas if verification starts producing them.

Signed desktop builds reach a machine without the in-app updater, or without any network, through
`scripts/release/install-offline.ps1`:

- It copies what it uses off the media into a directory only the current user can read, verifies
  that copy, and runs the copied installer, never the file on the media.
- It verifies with a pinned cosign (v3.1.3, by sha256), against a trust root provisioned separately
  from the media. It accepts only standardized Sigstore bundles, and only the publish workflow on
  main in this repository.
- It requires the ring decision on the media, for `-Ring` in `-FeedRepository`, to select exactly
  this manifest, not to be paused, and to be at or above `-MinimumGeneration`.
- It refuses a version older than the installed one unless the decision is a signed rollback or
  `-AllowDowngrade` is given, and treats an installed version it cannot read the same way.
- `-Headless` installs silently, and `-WhatIf` performs every check without installing.
- `-BreakGlass` installs a publisher-signed build without a ring decision and warns that no ring
  policy was applied.

It is also exercised on a Windows GitHub runner with a successful installer, an installer that
does nothing, one that writes the wrong `BUILD_INFO.txt`, and an inconsistent rollback decision;
only the first is accepted.
