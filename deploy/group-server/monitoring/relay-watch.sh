#!/usr/bin/env bash
# Classifies the small public /health reply without echoing its URL or body.
#
# The relay may be reachable through an address that names its operator. Workflow logs are
# retained and visible to more people than the relay, so the address and any unexpected health
# fields must never be printed here. The caller owns fetching; this script accepts a file to keep
# the input boundary testable and to make stale-deployment alerts reproducible.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: relay-watch.sh --health-file FILE --expected-commit SHA --expected-commit-utc UNIX [options]

Classify a relay's minimal public health response. Writes only status and a sanitized reason.

Options:
  --now-utc UNIX          Current UTC epoch seconds (default: current time).
  --deployment-grace-minutes N
                           Time allowed for the relay updater to reach a new expected commit
                           (default: 45). A matching commit is ready at any age.
  --github-output FILE    Write status/reason in GitHub output format as well as stdout.
EOF
}

health_file=""
expected_commit=""
expected_commit_utc=""
now_utc="$(date -u +%s)"
deployment_grace_minutes=45
github_output=""

while (($# > 0)); do
    case "$1" in
        --health-file) health_file="$2"; shift 2 ;;
        --expected-commit) expected_commit="$2"; shift 2 ;;
        --expected-commit-utc) expected_commit_utc="$2"; shift 2 ;;
        --now-utc) now_utc="$2"; shift 2 ;;
        --deployment-grace-minutes) deployment_grace_minutes="$2"; shift 2 ;;
        --github-output) github_output="$2"; shift 2 ;;
        --help) usage; exit 0 ;;
        *) printf 'relay-watch: unknown argument\n' >&2; usage >&2; exit 2 ;;
    esac
done

for command_name in jq grep; do
    command -v "$command_name" >/dev/null 2>&1 || {
        printf 'relay-watch: required command unavailable: %s\n' "$command_name" >&2
        exit 2
    }
done

if [[ ! -r "$health_file" || ! "$expected_commit" =~ ^[0-9a-fA-F]{7,64}$ ||
      ! "$expected_commit_utc" =~ ^[0-9]+$ || ! "$now_utc" =~ ^[0-9]+$ ||
      ! "$deployment_grace_minutes" =~ ^[0-9]+$ ]]; then
    printf 'relay-watch: invalid invocation\n' >&2
    exit 2
fi

status="failed"
reason="relay health response was invalid"
if jq -e '
    type == "object" and
    .status == "ok" and
    (.protocol | type == "number") and
    (.version | type == "string" and length <= 96) and
    (.commit | type == "string" and test("^[0-9a-fA-F]{7,64}$"))
  ' "$health_file" >/dev/null 2>&1; then
    actual_commit="$(jq -r '.commit' "$health_file")"
    expected_age_seconds=$((now_utc - expected_commit_utc))
    grace_seconds=$((deployment_grace_minutes * 60))
    if ((expected_age_seconds < 0)); then
        status="failed"
        reason="expected deployment time is in the future"
    elif [[ "${expected_commit,,}" == "${actual_commit,,}"* || "${actual_commit,,}" == "${expected_commit,,}"* ]]; then
        # A release may report Git's short SHA while Actions has the full SHA. Once the relay
        # matches the expected revision, the commit's age is irrelevant: a quiet main branch is
        # healthy, not a deployment incident.
        status="ready"
        reason="relay health is current"
    elif ((expected_age_seconds > grace_seconds)); then
        status="stale"
        reason="relay did not reach the expected deployment before the grace window elapsed"
    else
        # Publishing takes roughly eight minutes and the relay polls every 30±5 minutes. This
        # 45-minute window avoids alerting during a normal rollout while still bounding a miss.
        status="updating"
        reason="relay deployment is within the documented grace window"
    fi
fi

printf 'status=%s\nreason=%s\n' "$status" "$reason"
if [[ -n "$github_output" ]]; then
    printf 'status=%s\nreason=%s\n' "$status" "$reason" >> "$github_output"
fi

[[ "$status" == "ready" || "$status" == "updating" ]]
