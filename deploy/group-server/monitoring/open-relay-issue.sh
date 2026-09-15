#!/usr/bin/env bash
# Creates or updates an issue only after an exact-title lookup.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: open-relay-issue.sh --repository OWNER/REPO --title TITLE --body-file FILE
       [--on-existing comment|skip] [--existing-state open|all] [--label LABEL]
EOF
}

repository=""
title=""
body_file=""
on_existing="comment"
existing_state="open"
label=""

take_value() {
    if (($# < 2)) || [[ -z "$2" || "$2" == --* ]]; then
        printf 'open-relay-issue: option requires a value\n' >&2
        usage >&2
        exit 2
    fi
}

while (($# > 0)); do
    case "$1" in
        --repository) take_value "$@"; repository="$2"; shift 2 ;;
        --title) take_value "$@"; title="$2"; shift 2 ;;
        --body-file) take_value "$@"; body_file="$2"; shift 2 ;;
        --on-existing) take_value "$@"; on_existing="$2"; shift 2 ;;
        --existing-state) take_value "$@"; existing_state="$2"; shift 2 ;;
        --label) take_value "$@"; label="$2"; shift 2 ;;
        --help) usage; exit 0 ;;
        *) printf 'open-relay-issue: unknown argument\n' >&2; usage >&2; exit 2 ;;
    esac
done

for command_name in gh jq stat; do
    command -v "$command_name" >/dev/null 2>&1 || {
        printf 'open-relay-issue: required command unavailable: %s\n' "$command_name" >&2
        exit 2
    }
done

if [[ ! "$repository" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$ ||
      -z "$title" || "${#title}" -gt 160 || "$title" == *$'\n'* || "$title" == *$'\r'* ||
      ! -f "$body_file" || ! -r "$body_file" ||
      ( "$on_existing" != 'comment' && "$on_existing" != 'skip' ) ||
      ( "$existing_state" != 'open' && "$existing_state" != 'all' ) ||
      "${#label}" -gt 64 || "$label" == *$'\n'* || "$label" == *$'\r'* ]]; then
    printf 'open-relay-issue: invalid invocation\n' >&2
    exit 2
fi

if [[ "$(stat -c '%s' "$body_file")" -gt 8192 ]]; then
    printf 'open-relay-issue: issue body exceeded its validation limit\n' >&2
    exit 2
fi

# GitHub title search is intentionally only a bounded candidate query. The local equality filter
# is the authority: a prefix, suffix, or search-token collision must never own this incident.
listing="$(gh issue list --repo "$repository" --state "$existing_state" --limit 100 \
    --search "in:title \"$title\"" --json number,title)"
if ((${#listing} > 65536)); then
    printf 'open-relay-issue: issue listing exceeded its validation limit\n' >&2
    exit 1
fi

if ! existing="$(jq -er --arg title "$title" '
        if type != "array" or length > 100 or
           any(.[];
             type != "object" or
             (keys | sort) != ["number", "title"] or
             (.number | type != "number" or . != floor or . <= 0) or
             (.title | type != "string" or length > 256))
        then error("invalid issue listing")
        else .
        end |
        [ .[] | select(.title == $title) ] |
        if length > 1 then error("duplicate exact issue titles")
        elif length == 1 then .[0].number
        else ""
        end
    ' <<<"$listing")"; then
    printf 'open-relay-issue: issue listing was invalid or ambiguous\n' >&2
    exit 1
fi

if [[ -n "$existing" ]]; then
    if [[ "$on_existing" == 'comment' ]]; then
        gh issue comment "$existing" --repo "$repository" --body-file "$body_file"
    fi
    exit 0
fi

create=(gh issue create --repo "$repository" --title "$title" --body-file "$body_file")
if [[ -n "$label" ]]; then
    create+=(--label "$label")
fi
"${create[@]}"
