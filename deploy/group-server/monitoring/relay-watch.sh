#!/usr/bin/env bash
# Validates public relay liveness and the lineage of the build it names.
#
# This helper deliberately does not select or authenticate a release. The relay updater is the
# only component that holds the private-feed credential and Sigstore trust root, and its
# root-owned state is the authority for signed ring selection, pause, rollback, and freshness.
# The watch proves only that /health is bounded and well formed and that its commit is a known
# default-branch build. It prints fixed reasons so neither the URL nor response body can cross
# into durable Actions output.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: relay-watch.sh --health-file FILE --repository-root DIR
       --default-branch-commit SHA [--github-output FILE]
EOF
}

health_file=""
repository_root=""
default_branch_commit=""
github_output=""

take_value() {
    if (($# < 2)) || [[ -z "$2" || "$2" == --* ]]; then
        printf 'relay-watch: option requires a value\n' >&2
        usage >&2
        exit 2
    fi
}

while (($# > 0)); do
    case "$1" in
        --health-file) take_value "$@"; health_file="$2"; shift 2 ;;
        --repository-root) take_value "$@"; repository_root="$2"; shift 2 ;;
        --default-branch-commit) take_value "$@"; default_branch_commit="$2"; shift 2 ;;
        --github-output) take_value "$@"; github_output="$2"; shift 2 ;;
        --help) usage; exit 0 ;;
        *) printf 'relay-watch: unknown argument\n' >&2; usage >&2; exit 2 ;;
    esac
done

for command_name in git jq stat; do
    command -v "$command_name" >/dev/null 2>&1 || {
        printf 'relay-watch: required command unavailable: %s\n' "$command_name" >&2
        exit 2
    }
done

if [[ ! -f "$health_file" || ! -r "$health_file" ||
      ! -d "$repository_root" ||
      ! "$default_branch_commit" =~ ^[0-9a-fA-F]{40}$ ]] ||
   [[ "$(stat -c '%s' "$health_file")" -gt 16384 ]]; then
    printf 'relay-watch: invalid invocation\n' >&2
    exit 2
fi

finish() {
    local status="$1"
    local reason="$2"
    local result="$3"

    printf 'status=%s\nreason=%s\n' "$status" "$reason"
    if [[ -n "$github_output" ]]; then
        printf 'status=%s\nreason=%s\n' "$status" "$reason" >> "$github_output"
    fi
    exit "$result"
}

if ! jq -e '
    type == "object" and
    .status == "ok" and
    (.protocol | type == "number" and . == floor and . >= 1 and . <= 2147483647) and
    (.version | type == "string" and length >= 1 and length <= 96) and
    (.commit | type == "string" and test("^[0-9a-fA-F]{7,40}$"))
  ' "$health_file" >/dev/null 2>&1; then
    finish failed 'relay health response was invalid' 1
fi

if ! resolved_default="$(git -C "$repository_root" rev-parse --verify "$default_branch_commit^{commit}" 2>/dev/null)" ||
   [[ "$resolved_default" != "${default_branch_commit,,}" ]]; then
    finish failed 'default-branch repository evidence was invalid' 1
fi

actual_commit="$(jq -r '.commit' "$health_file")"
if ! resolved_actual="$(git -C "$repository_root" rev-parse --verify "$actual_commit^{commit}" 2>/dev/null)"; then
    finish failed 'relay health named a commit unavailable in repository history' 1
fi

if git -C "$repository_root" merge-base --is-ancestor "$resolved_actual" "$resolved_default"; then
    finish ready 'relay is live on a known default-branch build' 0
fi

if git -C "$repository_root" merge-base --is-ancestor "$resolved_default" "$resolved_actual"; then
    finish relay-ahead 'relay names a known commit ahead of the checked-out default branch' 1
fi

finish relay-diverged 'relay deployment is not on the checked-out default-branch lineage' 1
