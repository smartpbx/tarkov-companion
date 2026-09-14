#!/usr/bin/env bash
set -euo pipefail

readonly TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly TASK_FIXTURE_ROOT="${TASK_PROJECT_ROOT}/tests/safety-contract"

# Each entry names an API or package that performs a prohibited capability. Ordinary process
# lookup (OpenProcess for query access), physical hotkey observation (GetAsyncKeyState), and
# companion-window placement (SetWindowPos, an always-on-top companion, an owned companion
# dialog) are allowed, so they are deliberately absent. A click-through window is a single
# always-forbidden token (WS_EX_TRANSPARENT), so a plain substring scan catches it regardless of
# surrounding formatting. The other two overlay tripwires - a topmost layered window, and a
# companion window re-parented onto the game's own window - are deliberately NOT single-line
# substring patterns here: catching them that way (as before) required both flags, or the
# game's name, to sit on one line, which ordinary multi-line formatting and a neutrally named
# handle both defeat trivially. Those two are matched by scan_overlay_capabilities below, which
# looks for the capability - the flag combination, or a re-parent call sitting next to (in its
# own statement or an immediately adjacent one) a literal lookup of the game's real window title
# - rather than for a line shape or a naming convention. Every entry must be caught by its own
# line under tests/safety-contract/prohibited, so an entry nobody can trip fails the audit.
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
    # In-game overlay: the click-through style. The topmost+layered combination and the
    # re-parent-to-the-game-window capability are matched by scan_overlay_capabilities instead.
    'GameOverlay'
    'WS_EX_TRANSPARENT'
)
TASK_FORBIDDEN_PATTERN="$(IFS='|'; printf '%s' "${TASK_FORBIDDEN_PATTERNS[*]}")"
readonly TASK_FORBIDDEN_PATTERN

# Labels scan_overlay_capabilities reports (see TASK_OVERLAY_SCANNER below). Each must be
# exercised by its own prohibited fixture, exactly like TASK_FORBIDDEN_PATTERNS above.
readonly TASK_OVERLAY_CAPABILITIES=(
    'topmost layered overlay window'
    're-parented onto the game window'
)

# A companion Perl scanner for the two overlay capabilities that cannot be caught reliably by a
# single-line substring pattern (see the comment above TASK_FORBIDDEN_PATTERNS). Perl is used
# instead of rg/grep here because both capabilities need a small amount of structural context -
# ordinary multi-line formatting of one call, and a handle obtained in the immediately adjacent
# statement - that a line-oriented scanner cannot see. It never reports a naming convention as
# evidence: the game-window signal is the literal window title EFT is actually found by, not a
# developer's choice of identifier, so renaming a variable does not defeat it, and an unrelated
# mention of the title elsewhere in the file (a process-running check, a URL) does not trip it,
# because only the statement immediately touching the API call is considered.
read -r -d '' TASK_OVERLAY_SCANNER <<'PERL' || true
use strict;
use warnings;
use File::Find;

local $SIG{__WARN__} = sub {
    print STDERR "overlay scan error: $_[0]";
    exit 2;
};

my @files;
for my $root (@ARGV) {
    if (-d $root) {
        find({
            # With no_chdir, $_ is the whole path, so the build directories are matched by their
            # last component. Comparing $_ to 'bin' never matched, so after a build the scan read
            # Avalonia.Win32.dll under src/TarkovCompanion.App/bin and reported a topmost layered
            # window, and CI runs this audit after its Release build.
            wanted => sub {
                if (-d $_ && m{(?:^|/)(?:bin|obj)\z}) {
                    $File::Find::prune = 1;
                    return;
                }
                push @files, $File::Find::name if -f $_;
            },
            no_chdir => 1,
        }, $root);
    } elsif (-f $root) {
        push @files, $root;
    } else {
        print STDERR "overlay scan error: no such file or directory: $root\n";
        exit 2;
    }
}

# Each capability is one statement-shaped signal that may appear before or after the API call
# that performs it. Statements are joined only with an immediate neighbor (the statement right
# before or right after), so ordinary multi-line formatting of one call and a handle obtained in
# the adjacent statement are both caught, while an unrelated statement in between - or an
# unrelated mention elsewhere in the file - breaks the adjacency and is not.
my @capabilities = (
    [ 'a topmost layered overlay window (WS_EX_LAYERED + WS_EX_TOPMOST)',
      qr/\bWS_EX_LAYERED\b(?s:.*)\bWS_EX_TOPMOST\b/i,
      qr/\bWS_EX_TOPMOST\b(?s:.*)\bWS_EX_LAYERED\b/i ],
    [ 'a companion window re-parented onto the game window',
      qr/\bSetWindowLong(?:Ptr)?\s*\([^;]*\bGWLP?_HWNDPARENT\b(?s:.*)"(?:EscapeFromTarkov(?:\.exe)?|Escape From Tarkov|EFT(?:\.exe)?)"/i,
      qr/"(?:EscapeFromTarkov(?:\.exe)?|Escape From Tarkov|EFT(?:\.exe)?)"(?s:.*)\bSetWindowLong(?:Ptr)?\s*\([^;]*\bGWLP?_HWNDPARENT\b/i ],
);

for my $file (sort @files) {
    open(my $fh, '<', $file) or do {
        print STDERR "overlay scan error: cannot read file $file: $!\n";
        exit 2;
    };
    local $/;
    my $content = <$fh>;
    close $fh;
    next unless defined $content;

    # Split into statements, keeping each statement's trailing semicolon attached, and record
    # the 1-based line each statement starts on.
    my @chunks = split /(?<=;)/, $content;
    my @starts;
    my $line = 1;
    for my $chunk (@chunks) {
        push @starts, $line;
        $line += ($chunk =~ tr/\n//);
    }

    my %reported;
    for my $i (0 .. $#chunks) {
        my @windows;
        push @windows, [ $chunks[$i], $starts[$i] ];
        if ($i > 0) {
            push @windows, [ $chunks[$i - 1] . $chunks[$i], $starts[$i - 1] ];
        }
        if ($i < $#chunks) {
            push @windows, [ $chunks[$i] . $chunks[$i + 1], $starts[$i] ];
        }

        for my $cap (@capabilities) {
            my ($label, @patterns) = @$cap;
            for my $re (@patterns) {
                for my $window (@windows) {
                    my ($text, $base_line) = @$window;
                    while ($text =~ /$re/g) {
                        my $start_line = $base_line + (substr($text, 0, $-[0]) =~ tr/\n//);
                        my $end_line = $base_line + (substr($text, 0, $+[0]) =~ tr/\n//);
                        my $key = "$start_line\t$end_line\t$label";
                        next if $reported{$key}++;
                        print "$file\t$start_line\t$end_line\t$label\n";
                    }
                }
            }
        }
    }
}
PERL
readonly TASK_OVERLAY_SCANNER

if ! command -v perl >/dev/null 2>&1; then
    printf '%s\n' "Safety audit failed: required command is unavailable: perl" >&2
    exit 1
fi

scan_safety_patterns() {
    # Distinguishes "no match" (rg/grep exit 1) from a real scanner error (any other nonzero
    # exit - a bad pattern, a permissions problem, a crashed process). A scanner error used to
    # be swallowed by `|| true` and silently read as "nothing found," which would have let a
    # prohibited pattern pass audit undetected if the scan itself had failed partway through.
    local status=0
    local output=""
    # `!` collapses $? to a plain 0/1, so the real exit code is captured through `||` instead -
    # that preserves rg/grep's actual status (1 = no match, >1 = a real scanner error) while
    # staying exempt from `set -e`.
    if command -v rg >/dev/null 2>&1; then
        # The grep fallback naturally walks dotfiles and does not consult ignore files. Keep rg's
        # source universe identical: hidden and ignored source remains safety-relevant, generated
        # build output is explicitly pruned, and neither scanner follows directory symlinks. rg
        # does not follow them by default; lowercase grep -r preserves that boundary, whereas -R
        # would dereference a link and could expand an owned scan root outside the repository.
        output="$(rg -n -i --hidden --no-ignore \
            --glob '!bin/**' --glob '!obj/**' \
            --glob '!**/bin/**' --glob '!**/obj/**' \
            "${TASK_FORBIDDEN_PATTERN}" "$@")" || status=$?
    else
        output="$(grep -r -n -i -E \
            --exclude-dir=bin --exclude-dir=obj \
            "${TASK_FORBIDDEN_PATTERN}" "$@")" || status=$?
    fi
    if (( status > 1 )); then
        printf '%s\n' "Safety audit failed: scanner error (exit ${status}) while scanning: $*" >&2
        exit 1
    fi
    printf '%s' "${output}"
}

scan_overlay_capabilities() {
    local status=0
    local output=""
    output="$(perl - "$@" <<<"${TASK_OVERLAY_SCANNER}")" || status=$?
    if (( status != 0 )); then
        printf '%s\n' "Safety audit failed: overlay capability scanner error (exit ${status}) while scanning: $*" >&2
        exit 1
    fi
    printf '%s' "${output}"
}

# Keep the no-follow rule executable rather than relying on the option spelling above. The
# prohibited target sits beyond the scan root behind a directory symlink, the same traversal an
# in-repository link to an outside path would require. Both the selected line scanner and the
# Perl overlay scanner must leave it outside their universe. The temporary fixture and every
# cleanup target are bounded inside tests/safety-contract; no recursive removal is used.
TASK_NO_FOLLOW_ROOT="$(mktemp -d "${TASK_FIXTURE_ROOT}/.no-follow.XXXXXX")"
cleanup_no_follow_fixture() {
    rm -f -- \
        "${TASK_NO_FOLLOW_ROOT}/scan/outside" \
        "${TASK_NO_FOLLOW_ROOT}/outside/prohibited.txt"
    rmdir -- \
        "${TASK_NO_FOLLOW_ROOT}/scan" \
        "${TASK_NO_FOLLOW_ROOT}/outside" \
        "${TASK_NO_FOLLOW_ROOT}" 2>/dev/null || true
}
trap cleanup_no_follow_fixture EXIT
mkdir "${TASK_NO_FOLLOW_ROOT}/scan" "${TASK_NO_FOLLOW_ROOT}/outside"
printf '%s\n' 'ReadProcessMemory' 'WS_EX_LAYERED | WS_EX_TOPMOST' \
    > "${TASK_NO_FOLLOW_ROOT}/outside/prohibited.txt"
ln -s ../outside "${TASK_NO_FOLLOW_ROOT}/scan/outside"
TASK_NO_FOLLOW_PATTERN_MATCHES="$(scan_safety_patterns "${TASK_NO_FOLLOW_ROOT}/scan")"
TASK_NO_FOLLOW_OVERLAY_MATCHES="$(scan_overlay_capabilities "${TASK_NO_FOLLOW_ROOT}/scan")"
if [[ -n "${TASK_NO_FOLLOW_PATTERN_MATCHES}" || -n "${TASK_NO_FOLLOW_OVERLAY_MATCHES}" ]]; then
    printf '%s\n' "Safety audit self-test failed: a scanner followed a directory symlink outside its scan root." >&2
    exit 1
fi
cleanup_no_follow_fixture
trap - EXIT

TASK_SAFETY_MATCHES="$(scan_safety_patterns \
    "${TASK_PROJECT_ROOT}/src" \
    "${TASK_PROJECT_ROOT}/Directory.Build.props" \
    "${TASK_PROJECT_ROOT}/Directory.Packages.props")"
TASK_OVERLAY_MATCHES="$(scan_overlay_capabilities \
    "${TASK_PROJECT_ROOT}/src" \
    "${TASK_PROJECT_ROOT}/Directory.Build.props" \
    "${TASK_PROJECT_ROOT}/Directory.Packages.props")"

if [[ -n "${TASK_SAFETY_MATCHES}" || -n "${TASK_OVERLAY_MATCHES}" ]]; then
    [[ -n "${TASK_SAFETY_MATCHES}" ]] && printf '%s\n' "${TASK_SAFETY_MATCHES}"
    [[ -n "${TASK_OVERLAY_MATCHES}" ]] && printf '%s\n' "${TASK_OVERLAY_MATCHES}"
    printf '%s\n' "Safety audit failed: prohibited API or dependency pattern found." >&2
    exit 1
fi

# git grep's exit code is 0 for a match (failure here), 1 for no match, and >1 for a real error
# (bad pattern, not a git repo). Only exit 1 may fall through; anything else must fail closed
# instead of being read as "no match" the way a bare `if git grep ...; then` would read it.
if git -C "${TASK_PROJECT_ROOT}" grep -n -I -i -E \
    '<PackageReference[^>]+Include="(SharpPcap|PacketDotNet|EasyHook|MemorySharp|GameOverlay)' \
    -- '*.csproj' '*.props' '*.targets'; then
    printf '%s\n' "Safety audit failed: prohibited package reference found." >&2
    exit 1
else
    TASK_GIT_GREP_STATUS=$?
    if (( TASK_GIT_GREP_STATUS > 1 )); then
        printf '%s\n' "Safety audit failed: git grep error (exit ${TASK_GIT_GREP_STATUS}) while scanning package references." >&2
        exit 1
    fi
fi

# A missing fixture directory would otherwise pass both self-tests vacuously.
for TASK_FIXTURE_KIND in allowed prohibited; do
    if [[ -z "$(find "${TASK_FIXTURE_ROOT}/${TASK_FIXTURE_KIND}" -type f -print -quit 2>/dev/null)" ]]; then
        printf '%s\n' "Safety audit self-test failed: no ${TASK_FIXTURE_KIND} fixtures in ${TASK_FIXTURE_ROOT}." >&2
        exit 1
    fi
done

TASK_ALLOWED_MATCHES="$(scan_safety_patterns "${TASK_FIXTURE_ROOT}/allowed")"
TASK_ALLOWED_OVERLAY_MATCHES="$(scan_overlay_capabilities "${TASK_FIXTURE_ROOT}/allowed")"
if [[ -n "${TASK_ALLOWED_MATCHES}" || -n "${TASK_ALLOWED_OVERLAY_MATCHES}" ]]; then
    [[ -n "${TASK_ALLOWED_MATCHES}" ]] && printf '%s\n' "${TASK_ALLOWED_MATCHES}"
    [[ -n "${TASK_ALLOWED_OVERLAY_MATCHES}" ]] && printf '%s\n' "${TASK_ALLOWED_OVERLAY_MATCHES}"
    printf '%s\n' "Safety audit self-test failed: an allowed fixture was rejected." >&2
    exit 1
fi

# These two files are deliberately invisible to rg's defaults: one is a dotfile and the other is
# named by tests/safety-contract/.ignore. Both must still belong to the same scan universe as the
# grep fallback. Checking the reported path (not just another matching line) makes each flag a
# ratchet rather than an unexercised option.
TASK_PROHIBITED_PATTERN_MATCHES="$(scan_safety_patterns "${TASK_FIXTURE_ROOT}/prohibited")"
for TASK_UNIVERSE_FIXTURE in \
    "${TASK_FIXTURE_ROOT}/prohibited/.hidden-game-memory.cs" \
    "${TASK_FIXTURE_ROOT}/prohibited/ignored-game-input.cs"; do
    if ! grep -q -F "${TASK_UNIVERSE_FIXTURE}:" <<<"${TASK_PROHIBITED_PATTERN_MATCHES}"; then
        printf '%s\n' "Safety audit self-test failed: scanner skipped source fixture: ${TASK_UNIVERSE_FIXTURE#"${TASK_PROJECT_ROOT}/"}" >&2
        exit 1
    fi
done

# Every overlay-capability match against the prohibited fixtures, computed once and reused below
# both to confirm every fixture line is covered and to confirm every capability is exercised.
TASK_OVERLAY_PROHIBITED_MATCHES="$(scan_overlay_capabilities "${TASK_FIXTURE_ROOT}/prohibited")"

overlay_line_is_covered() {
    local fixture_path="$1"
    local line_number="$2"
    local match_file match_start match_end match_label
    while IFS=$'\t' read -r match_file match_start match_end match_label; do
        [[ -z "${match_file}" ]] && continue
        if [[ "${match_file}" == "${fixture_path}" ]] \
            && (( line_number >= match_start && line_number <= match_end )); then
            return 0
        fi
    done <<<"${TASK_OVERLAY_PROHIBITED_MATCHES}"
    return 1
}

TASK_PROHIBITED_LINES=""

# Every line must be caught on its own - by the substring pattern list or by
# scan_overlay_capabilities - so one detected line cannot hide an undetected one.
# Files are read one at a time so a diagnostic names its file and line, and a final line without
# a newline is still checked rather than joined to the next file's first line.
while IFS= read -r -d '' TASK_FORBIDDEN_FIXTURE; do
    TASK_FORBIDDEN_LINE_NUMBER=0
    while IFS= read -r TASK_FORBIDDEN_LINE || [[ -n "${TASK_FORBIDDEN_LINE}" ]]; do
        TASK_FORBIDDEN_LINE_NUMBER=$((TASK_FORBIDDEN_LINE_NUMBER + 1))
        [[ -z "${TASK_FORBIDDEN_LINE//[[:space:]]/}" ]] && continue
        if grep -q -i -E "${TASK_FORBIDDEN_PATTERN}" <<<"${TASK_FORBIDDEN_LINE}"; then
            TASK_PROHIBITED_LINES+="${TASK_FORBIDDEN_LINE}"$'\n'
            continue
        fi
        if overlay_line_is_covered "${TASK_FORBIDDEN_FIXTURE}" "${TASK_FORBIDDEN_LINE_NUMBER}"; then
            continue
        fi
        printf '%s\n' "Safety audit self-test failed: prohibited fixture was not detected: ${TASK_FORBIDDEN_FIXTURE#"${TASK_PROJECT_ROOT}/"}:${TASK_FORBIDDEN_LINE_NUMBER}" >&2
        exit 1
    done < "${TASK_FORBIDDEN_FIXTURE}"
done < <(find "${TASK_FIXTURE_ROOT}/prohibited" -type f -print0 | sort -z)

# And every substring pattern must catch a fixture line of its own.
for TASK_FORBIDDEN_ENTRY in "${TASK_FORBIDDEN_PATTERNS[@]}"; do
    if ! grep -q -i -E "${TASK_FORBIDDEN_ENTRY}" <<<"${TASK_PROHIBITED_LINES}"; then
        printf '%s\n' "Safety audit self-test failed: no prohibited fixture exercises pattern: ${TASK_FORBIDDEN_ENTRY}" >&2
        exit 1
    fi
done

# And every overlay capability must catch a fixture of its own.
for TASK_OVERLAY_CAPABILITY in "${TASK_OVERLAY_CAPABILITIES[@]}"; do
    if ! grep -q -F "${TASK_OVERLAY_CAPABILITY}" <<<"${TASK_OVERLAY_PROHIBITED_MATCHES}"; then
        printf '%s\n' "Safety audit self-test failed: no prohibited fixture exercises overlay capability: ${TASK_OVERLAY_CAPABILITY}" >&2
        exit 1
    fi
done

printf '%s\n' "Safety audit passed: no prohibited integration pattern found in source or project files."
