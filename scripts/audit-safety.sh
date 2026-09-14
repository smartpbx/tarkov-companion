#!/usr/bin/env bash
set -euo pipefail

readonly TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Ordinary process/window discovery, Print Screen observation, and companion-window placement
# are allowed. Match the APIs that perform the prohibited capability, not adjacent API names.
readonly TASK_FORBIDDEN_PATTERN='ReadProcessMemory|WriteProcessMemory|VirtualAllocEx|VirtualProtectEx|CreateRemoteThread|NtReadVirtualMemory|NtWriteVirtualMemory|NtQueryVirtualMemory|SetWindowsHookEx|SendInput|mouse_event|keybd_event|InputSimulator|WindowsInput|WinDivert|SharpPcap|PacketDotNet|SocketType\.Raw|IOControlCode\.ReceiveAll|SIO_RCVALL|pcap_open_live|EasyHook|Reloaded\.Hooks|MemorySharp|GameOverlay|Vortice\.Direct3D.*Hook|Direct3D.*PresentHook'
readonly TASK_FIXTURE_ROOT="${TASK_PROJECT_ROOT}/tests/safety-contract"

scan_safety_patterns() {
    if command -v rg >/dev/null 2>&1; then
        rg -n -i "${TASK_FORBIDDEN_PATTERN}" "$@" || true
    else
        grep -R -n -i -E \
            --exclude-dir=bin \
            --exclude-dir=obj \
            "${TASK_FORBIDDEN_PATTERN}" "$@" || true
    fi
}

TASK_SAFETY_MATCHES="$(scan_safety_patterns \
    "${TASK_PROJECT_ROOT}/src" \
    "${TASK_PROJECT_ROOT}/Directory.Build.props" \
    "${TASK_PROJECT_ROOT}/Directory.Packages.props")"

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

TASK_ALLOWED_MATCHES="$(scan_safety_patterns "${TASK_FIXTURE_ROOT}/allowed")"
if [[ -n "${TASK_ALLOWED_MATCHES}" ]]; then
    printf '%s\n' "${TASK_ALLOWED_MATCHES}"
    printf '%s\n' "Safety audit self-test failed: an allowed fixture was rejected." >&2
    exit 1
fi

while IFS= read -r TASK_FORBIDDEN_FIXTURE; do
    if [[ -z "$(scan_safety_patterns "${TASK_FORBIDDEN_FIXTURE}")" ]]; then
        printf '%s\n' "Safety audit self-test failed: prohibited fixture was not detected: ${TASK_FORBIDDEN_FIXTURE}" >&2
        exit 1
    fi
done < <(find "${TASK_FIXTURE_ROOT}/prohibited" -type f -print | sort)

printf '%s\n' "Safety audit passed: no prohibited integration pattern found in source or project files."
