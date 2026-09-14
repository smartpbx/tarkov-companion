#!/usr/bin/env bash
set -euo pipefail

readonly TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly TASK_FIXTURE_ROOT="${TASK_PROJECT_ROOT}/tests/safety-contract"

# Each entry names an API or package that performs a prohibited capability. Ordinary process
# lookup (OpenProcess for query access), physical hotkey observation (GetAsyncKeyState), and
# companion-window placement (SetWindowPos, an always-on-top companion, an owned companion
# dialog) are allowed, so they are deliberately absent. The overlay tripwire is the capability
# itself: a click-through window, a topmost layered window, or a window re-owned by the game's
# window. Every entry must be caught by its own line under tests/safety-contract/prohibited, so
# an entry nobody can trip fails the audit.
readonly TASK_FORBIDDEN_PATTERNS=(
    # Game process memory and injection.
    'ReadProcessMemory'
    'WriteProcessMemory'
    'VirtualAllocEx'
    'VirtualProtectEx'
    'CreateRemoteThread'
    'NtReadVirtualMemory'
    'NtWriteVirtualMemory'
    'NtQueryVirtualMemory'
    'MemorySharp'
    # Hooks.
    'SetWindowsHookEx'
    'EasyHook'
    'Reloaded\.Hooks'
    'Vortice\.Direct3D.*Hook'
    'Direct3D.*PresentHook'
    # Generated keyboard, mouse, or controller input.
    'SendInput'
    'mouse_event'
    'keybd_event'
    'InputSimulator'
    'WindowsInput'
    'ViGEm'
    'vJoy'
    # Packet capture and inspection.
    'WinDivert'
    'SharpPcap'
    'PacketDotNet'
    'SocketType\.Raw'
    'IOControlCode\.ReceiveAll'
    'SIO_RCVALL'
    'pcap_open_live'
    # In-game overlay.
    'GameOverlay'
    'WS_EX_TRANSPARENT'
    'WS_EX_TOPMOST.*WS_EX_LAYERED'
    'WS_EX_LAYERED.*WS_EX_TOPMOST'
    'SetWindowLong(Ptr)?[^;]*GWLP?_HWNDPARENT[^;]*(game|eft|tarkov)'
)
TASK_FORBIDDEN_PATTERN="$(IFS='|'; printf '%s' "${TASK_FORBIDDEN_PATTERNS[*]}")"
readonly TASK_FORBIDDEN_PATTERN

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

TASK_PROHIBITED_LINES=""

# Every line must be caught on its own, so one detected line cannot hide an undetected one.
# Files are read one at a time so a diagnostic names its file and line, and a final line without
# a newline is still checked rather than joined to the next file's first line.
while IFS= read -r -d '' TASK_FORBIDDEN_FIXTURE; do
    TASK_FORBIDDEN_LINE_NUMBER=0
    while IFS= read -r TASK_FORBIDDEN_LINE || [[ -n "${TASK_FORBIDDEN_LINE}" ]]; do
        TASK_FORBIDDEN_LINE_NUMBER=$((TASK_FORBIDDEN_LINE_NUMBER + 1))
        [[ -z "${TASK_FORBIDDEN_LINE//[[:space:]]/}" ]] && continue
        if ! grep -q -i -E "${TASK_FORBIDDEN_PATTERN}" <<<"${TASK_FORBIDDEN_LINE}"; then
            printf '%s\n' "Safety audit self-test failed: prohibited fixture was not detected: ${TASK_FORBIDDEN_FIXTURE#"${TASK_PROJECT_ROOT}/"}:${TASK_FORBIDDEN_LINE_NUMBER}" >&2
            exit 1
        fi
        TASK_PROHIBITED_LINES+="${TASK_FORBIDDEN_LINE}"$'\n'
    done < "${TASK_FORBIDDEN_FIXTURE}"
done < <(find "${TASK_FIXTURE_ROOT}/prohibited" -type f -print0 | sort -z)

# And every pattern must catch a fixture line of its own.
for TASK_FORBIDDEN_ENTRY in "${TASK_FORBIDDEN_PATTERNS[@]}"; do
    if ! grep -q -i -E "${TASK_FORBIDDEN_ENTRY}" <<<"${TASK_PROHIBITED_LINES}"; then
        printf '%s\n' "Safety audit self-test failed: no prohibited fixture exercises pattern: ${TASK_FORBIDDEN_ENTRY}" >&2
        exit 1
    fi
done

printf '%s\n' "Safety audit passed: no prohibited integration pattern found in source or project files."
