#!/usr/bin/env bash
# Validates a complete bounded report listing before opening any public issue.
set -euo pipefail

readonly MAXIMUM_LIST_BYTES=65536
readonly MAXIMUM_REPORTS=50

usage() {
    cat <<'EOF'
Usage: file-relay-reports.sh --list-file FILE --repository OWNER/REPO
EOF
}

list_file=""
repository=""

take_value() {
    if (($# < 2)) || [[ -z "$2" || "$2" == --* ]]; then
        printf 'file-relay-reports: option requires a value\n' >&2
        usage >&2
        exit 2
    fi
}

while (($# > 0)); do
    case "$1" in
        --list-file) take_value "$@"; list_file="$2"; shift 2 ;;
        --repository) take_value "$@"; repository="$2"; shift 2 ;;
        --help) usage; exit 0 ;;
        *) printf 'file-relay-reports: unknown argument\n' >&2; usage >&2; exit 2 ;;
    esac
done

for command_name in jq mktemp stat; do
    command -v "$command_name" >/dev/null 2>&1 || {
        printf 'file-relay-reports: required command unavailable: %s\n' "$command_name" >&2
        exit 2
    }
done

if [[ ! -f "$list_file" || ! -r "$list_file" ||
      ! "$repository" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$ ]]; then
    printf 'file-relay-reports: invalid invocation\n' >&2
    exit 2
fi

if [[ "$(stat -c '%s' "$list_file")" -gt "$MAXIMUM_LIST_BYTES" ]]; then
    printf 'file-relay-reports: report listing exceeded the public-issue validation limit\n' >&2
    exit 1
fi

# Validate the entire document before the loop. Every value later interpolated into Markdown is
# constrained to an inert alphabet or a bounded integer, and duplicate references are refused.
if ! jq -e --argjson maximum "$MAXIMUM_REPORTS" '
    def utc:
      type == "string" and
      length <= 35 and
      test("^[0-9]{4}-(0[1-9]|1[0-2])-([0-2][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\\.[0-9]{1,7})?(Z|\\+00:00)$") and
      ((sub("\\.[0-9]{1,7}(Z|\\+00:00)$"; "Z") | sub("\\+00:00$"; "Z") | fromdateiso8601?) | type == "number");
    type == "array" and
    length <= $maximum and
    all(.[];
      type == "object" and
      (keys | sort) == ["bytes", "receivedUtc", "reference"] and
      (.reference | type == "string" and test("^[0-9a-f]{12}$")) and
      (.bytes | type == "number" and . == floor and . >= 1 and . <= 65536) and
      (.receivedUtc | utc)
    ) and
    ([.[].reference] | unique | length) == length
  ' "$list_file" >/dev/null 2>&1; then
    printf 'file-relay-reports: report listing failed closed-schema validation\n' >&2
    exit 1
fi

task_temp="$(mktemp -d "${TMPDIR:-/tmp}/tarkov-relay-reports.XXXXXX")"
trap 'rm -rf -- "$task_temp"' EXIT
jq -r '.[] | [.reference, (.bytes | tostring), .receivedUtc] | @tsv' "$list_file" |
while IFS=$'\t' read -r reference bytes received_utc; do
    body_file="${task_temp}/${reference}.md"
    printf '%s\n' \
      "A player sent a diagnostic report from the companion's Report a problem button." \
      "" \
      "Reference: ${reference}" \
      "Size: ${bytes} bytes" \
      "Received: ${received_utc}" \
      "" \
      "The report body and relay address are not attached to this public issue." \
      "Issue #310 owns aligning the listed reference with the restricted read route; until it lands, this issue is not evidence that retrieval by this reference works." \
      > "$body_file"
    bash "$(dirname "${BASH_SOURCE[0]}")/open-relay-issue.sh" \
      --repository "$repository" \
      --title "Problem report ${reference}" \
      --body-file "$body_file" \
      --on-existing skip \
      --existing-state all \
      --label problem-report
    printf 'recorded one validated report reference\n'
done
