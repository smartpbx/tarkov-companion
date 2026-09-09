#!/usr/bin/env bash
set -euo pipefail

readonly TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly TASK_FORBIDDEN_PATTERN='OpenProcess|ReadProcessMemory|WriteProcessMemory|VirtualAllocEx|CreateRemoteThread|NtQueryVirtualMemory|SetWindowsHookEx|SendInput|mouse_event|keybd_event|GetAsyncKeyState|WinDivert|SharpPcap|PacketDotNet|EasyHook|Reloaded\.Hooks|MemorySharp|GameOverlay|Vortice\.Direct3D.*Hook|SetWindowPos|WS_EX_TOPMOST'

if ! command -v rg >/dev/null 2>&1; then
    printf '%s\n' "Safety audit failed: ripgrep (rg) is required." >&2
    exit 1
fi

if rg -n -i "${TASK_FORBIDDEN_PATTERN}" \
    "${TASK_PROJECT_ROOT}/src" \
    "${TASK_PROJECT_ROOT}/Directory.Build.props" \
    "${TASK_PROJECT_ROOT}/Directory.Packages.props"; then
    printf '%s\n' "Safety audit failed: prohibited API or dependency pattern found." >&2
    exit 1
fi

if rg -n -i '<PackageReference[^>]+Include="(SharpPcap|PacketDotNet|EasyHook|MemorySharp|GameOverlay)' "${TASK_PROJECT_ROOT}"; then
    printf '%s\n' "Safety audit failed: prohibited package reference found." >&2
    exit 1
fi

printf '%s\n' "Safety audit passed: no prohibited integration pattern found in source or project files."
