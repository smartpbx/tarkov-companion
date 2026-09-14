#!/usr/bin/env bash
set -euo pipefail

readonly TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# The v2 contract adds capability patterns; it does not relax v1 ones. OpenProcess is the gateway
# to process memory, GetAsyncKeyState polls game input, and SetWindowPos/WS_EX_TOPMOST is how a
# window is pinned over the game. Ordinary discovery (Process.GetProcessesByName, EnumWindows)
# and visible capture do not need any of them. ViGEm/vJoy synthesize controller input.
readonly TASK_FORBIDDEN_PATTERN='OpenProcess|ReadProcessMemory|WriteProcessMemory|VirtualAllocEx|VirtualProtectEx|CreateRemoteThread|NtReadVirtualMemory|NtWriteVirtualMemory|NtQueryVirtualMemory|SetWindowsHookEx|SendInput|mouse_event|keybd_event|GetAsyncKeyState|InputSimulator|WindowsInput|ViGEm|vJoy|WinDivert|SharpPcap|PacketDotNet|SocketType\.Raw|IOControlCode\.ReceiveAll|SIO_RCVALL|pcap_open_live|EasyHook|Reloaded\.Hooks|MemorySharp|GameOverlay|Vortice\.Direct3D.*Hook|Direct3D.*PresentHook|SetWindowPos|WS_EX_TOPMOST'
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

# A missing fixture directory would otherwise pass both self-tests vacuously.
for TASK_FIXTURE_KIND in allowed prohibited; do
    if [[ -z "$(find "${TASK_FIXTURE_ROOT}/${TASK_FIXTURE_KIND}" -type f -print -quit 2>/dev/null)" ]]; then
        printf '%s\n' "Safety audit self-test failed: no ${TASK_FIXTURE_KIND} fixtures in ${TASK_FIXTURE_ROOT}." >&2
        exit 1
    fi
done

TASK_ALLOWED_MATCHES="$(scan_safety_patterns "${TASK_FIXTURE_ROOT}/allowed")"
if [[ -n "${TASK_ALLOWED_MATCHES}" ]]; then
    printf '%s\n' "${TASK_ALLOWED_MATCHES}"
    printf '%s\n' "Safety audit self-test failed: an allowed fixture was rejected." >&2
    exit 1
fi

# Every line must be caught on its own, so one detected line cannot hide an undetected one.
while IFS= read -r TASK_FORBIDDEN_FIXTURE; do
    TASK_FORBIDDEN_LINE_NUMBER=0
    while IFS= read -r TASK_FORBIDDEN_LINE || [[ -n "${TASK_FORBIDDEN_LINE}" ]]; do
        TASK_FORBIDDEN_LINE_NUMBER=$((TASK_FORBIDDEN_LINE_NUMBER + 1))
        [[ -z "${TASK_FORBIDDEN_LINE//[[:space:]]/}" ]] && continue
        if ! grep -q -i -E "${TASK_FORBIDDEN_PATTERN}" <<<"${TASK_FORBIDDEN_LINE}"; then
            printf '%s\n' "Safety audit self-test failed: prohibited fixture was not detected: ${TASK_FORBIDDEN_FIXTURE}:${TASK_FORBIDDEN_LINE_NUMBER}" >&2
            exit 1
        fi
    done < "${TASK_FORBIDDEN_FIXTURE}"
done < <(find "${TASK_FIXTURE_ROOT}/prohibited" -type f -print | sort)

printf '%s\n' "Safety audit passed: no prohibited integration pattern found in source or project files."
