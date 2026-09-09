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

The log watcher tails newly appended lines in ordinary `*.log` files with read-sharing enabled and feeds the tolerant application parser. Existing bytes are not replayed on watcher startup. Unknown lines are ignored. The screenshot watcher emits only newly created or renamed PNG/JPEG paths; simulator-named paths are ignored unless DeveloperMode was explicit. Both watchers propagate cancellation.

## Secrets

The secret store encrypts UTF-8 values with Windows DPAPI CurrentUser scope and app-specific entropy. Disk filenames are SHA-256 hashes of logical keys, writes are replaced atomically, and plaintext is not written to disk. DPAPI ciphertext is user-and-machine scoped and is not a portable backup format.

## Windows VM validation still required

Linux proves contract behavior and Windows gating but cannot exercise User32, GDI, Shcore, or DPAPI. The Windows VM must verify:

- real and simulator window bounds at 100%, 125%, and mixed-monitor DPI;
- window capture, explicitly allowed desktop fallback, minimized-window behavior, and BGRA orientation;
- hotkey registration, receipt, collision reporting, unregister, and clean shutdown;
- multiple-monitor bounds, primary identity, and scale;
- registry/folder path discovery against an installed or synthetic layout;
- concurrent log writes and screenshot create/rename notifications;
- DPAPI set/get/delete and inability to decrypt from another Windows user.

Direct validation against a real EFT installation remains a separate checklist in `LIVE_EFT_VALIDATION.md` and must not be claimed from simulator or VM-only evidence.
