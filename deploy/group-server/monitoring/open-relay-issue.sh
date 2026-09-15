#!/usr/bin/env bash
# Creates the one relay incident, or adds evidence to its existing issue.
#
# Keeping this tiny gh boundary testable prevents a later workflow edit from turning an hourly
# watch into an hourly issue flood. Bodies are supplied by the caller and must already be safe
# for the public repository; this script never prints a relay URL or report body.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: open-relay-issue.sh --repository OWNER/REPO --title TITLE --body-file FILE [--on-existing comment|skip]
EOF
}

repository=""
title=""
body_file=""
on_existing="comment"

while (($# > 0)); do
    case "$1" in
        --repository) repository="$2"; shift 2 ;;
        --title) title="$2"; shift 2 ;;
        --body-file) body_file="$2"; shift 2 ;;
        --on-existing) on_existing="$2"; shift 2 ;;
        --help) usage; exit 0 ;;
        *) printf 'open-relay-issue: unknown argument\n' >&2; usage >&2; exit 2 ;;
    esac
done

if [[ -z "$repository" || -z "$title" || ! -r "$body_file" ||
      ( "$on_existing" != 'comment' && "$on_existing" != 'skip' ) ]]; then
    printf 'open-relay-issue: invalid invocation\n' >&2
    exit 2
fi

existing="$(gh issue list --repo "$repository" --state open \
    --search "in:title \"$title\"" --json number --jq '.[0].number // empty')"
if [[ -n "$existing" ]]; then
    if [[ "$on_existing" == 'comment' ]]; then
        gh issue comment "$existing" --repo "$repository" --body-file "$body_file"
    fi
else
    gh issue create --repo "$repository" --title "$title" --body-file "$body_file"
fi
