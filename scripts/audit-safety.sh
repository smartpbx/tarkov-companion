#!/usr/bin/env bash
set -euo pipefail

readonly TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly TASK_FORBIDDEN_PATTERN='OpenProcess|ReadProcessMemory|WriteProcessMemory|VirtualAllocEx|CreateRemoteThread|NtQueryVirtualMemory|SetWindowsHookEx|SendInput|mouse_event|keybd_event|GetAsyncKeyState|WinDivert|SharpPcap|PacketDotNet|EasyHook|Reloaded\.Hooks|MemorySharp|GameOverlay|Vortice\.Direct3D.*Hook|SetWindowPos|WS_EX_TOPMOST'

if command -v rg >/dev/null 2>&1; then
    TASK_SAFETY_MATCHES="$(rg -n -i "${TASK_FORBIDDEN_PATTERN}" \
        "${TASK_PROJECT_ROOT}/src" \
        "${TASK_PROJECT_ROOT}/Directory.Build.props" \
        "${TASK_PROJECT_ROOT}/Directory.Packages.props" || true)"
else
    TASK_SAFETY_MATCHES="$(grep -R -n -i -E \
        --exclude-dir=bin \
        --exclude-dir=obj \
        "${TASK_FORBIDDEN_PATTERN}" \
        "${TASK_PROJECT_ROOT}/src" \
        "${TASK_PROJECT_ROOT}/Directory.Build.props" \
        "${TASK_PROJECT_ROOT}/Directory.Packages.props" || true)"
fi

if [[ -n "${TASK_SAFETY_MATCHES}" ]]; then
    printf '%s\n' "${TASK_SAFETY_MATCHES}"
    printf '%s\n' "Safety audit failed: prohibited API or dependency pattern found." >&2
    exit 1
fi

if git -C "${TASK_PROJECT_ROOT}" grep -n -I -i -E \
    '<PackageReference[^>]+Include="(SharpPcap|PacketDotNet|EasyHook|MemorySharp|GameOverlay)' \
    -- '*.csproj' '*.props' '*.targets'; then
    printf '%s\n' "Safety audit failed: prohibited package reference found." >&2
    exit 1
fi

printf '%s\n' "Safety audit passed: no prohibited integration pattern found in source or project files."
