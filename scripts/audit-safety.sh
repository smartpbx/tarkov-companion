#!/usr/bin/env bash
set -euo pipefail

readonly TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly TASK_FORBIDDEN_PATTERN='ReadProcessMemory|WriteProcessMemory|VirtualAllocEx|CreateRemoteThread|NtQueryVirtualMemory|SetWindowsHookEx|SendInput|mouse_event|keybd_event|SharpPcap|PacketDotNet|EasyHook|Reloaded\.Hooks|MemorySharp|GameOverlay|Vortice\.Direct3D.*Hook'

if rg -n -i "${TASK_FORBIDDEN_PATTERN}" \
    "${TASK_PROJECT_ROOT}/src" \
    "${TASK_PROJECT_ROOT}"/*.props \
    "${TASK_PROJECT_ROOT}"/*.csproj 2>/dev/null; then
    printf '%s\n' "Safety audit failed: prohibited API or dependency pattern found." >&2
    exit 1
fi

if rg -n -i '<PackageReference[^>]+Include="(SharpPcap|PacketDotNet|EasyHook|MemorySharp|GameOverlay)' "${TASK_PROJECT_ROOT}"; then
    printf '%s\n' "Safety audit failed: prohibited package reference found." >&2
    exit 1
fi

printf '%s\n' "Safety audit passed: no prohibited integration pattern found in source or project files."
