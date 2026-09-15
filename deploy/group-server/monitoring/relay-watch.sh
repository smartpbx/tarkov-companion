#!/usr/bin/env bash
# Classifies public relay health against one verified dev/update.json publication.
#
# The caller fetches three deliberately small evidence files: release-asset metadata from the
# authenticated GitHub API, the update.json bytes whose API digest was checked, and the Actions
# run named by that manifest. This helper revalidates the complete chain before comparing commits.
# It prints only an enumerated status and a fixed reason; no URL or response body can cross into
# durable Actions output.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: relay-watch.sh --health-file FILE --update-file FILE --release-evidence-file FILE
       --run-evidence-file FILE --repository-root DIR --default-branch NAME
       --default-branch-commit SHA [options]

Options:
  --now-utc UNIX                  Current UTC epoch seconds (default: current time).
  --deployment-grace-minutes N   Relay updater window after publication (default: 45).
  --publication-grace-minutes N  Verification/publication window after a main commit (default: 45).
  --github-output FILE            Also write status/reason as GitHub step output.
EOF
}

health_file=""
update_file=""
release_evidence_file=""
run_evidence_file=""
repository_root=""
default_branch=""
default_branch_commit=""
now_utc="$(date -u +%s)"
deployment_grace_minutes=45
publication_grace_minutes=45
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
        --update-file) take_value "$@"; update_file="$2"; shift 2 ;;
        --release-evidence-file) take_value "$@"; release_evidence_file="$2"; shift 2 ;;
        --run-evidence-file) take_value "$@"; run_evidence_file="$2"; shift 2 ;;
        --repository-root) take_value "$@"; repository_root="$2"; shift 2 ;;
        --default-branch) take_value "$@"; default_branch="$2"; shift 2 ;;
        --default-branch-commit) take_value "$@"; default_branch_commit="$2"; shift 2 ;;
        --now-utc) take_value "$@"; now_utc="$2"; shift 2 ;;
        --deployment-grace-minutes) take_value "$@"; deployment_grace_minutes="$2"; shift 2 ;;
        --publication-grace-minutes) take_value "$@"; publication_grace_minutes="$2"; shift 2 ;;
        --github-output) take_value "$@"; github_output="$2"; shift 2 ;;
        --help) usage; exit 0 ;;
        *) printf 'relay-watch: unknown argument\n' >&2; usage >&2; exit 2 ;;
    esac
done

for command_name in git jq sha256sum stat; do
    command -v "$command_name" >/dev/null 2>&1 || {
        printf 'relay-watch: required command unavailable: %s\n' "$command_name" >&2
        exit 2
    }
done

if [[ ! -f "$health_file" || ! -r "$health_file" ||
      ! -f "$update_file" || ! -r "$update_file" ||
      ! -f "$release_evidence_file" || ! -r "$release_evidence_file" ||
      ! -f "$run_evidence_file" || ! -r "$run_evidence_file" ||
      ! -d "$repository_root" ||
      ! "$default_branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$ ||
      ! "$default_branch_commit" =~ ^[0-9a-fA-F]{40}$ ||
      ! "$now_utc" =~ ^[0-9]{1,12}$ ||
      ! "$deployment_grace_minutes" =~ ^[0-9]{1,4}$ ||
      ! "$publication_grace_minutes" =~ ^[0-9]{1,4}$ ]]; then
    printf 'relay-watch: invalid invocation\n' >&2
    exit 2
fi

now_epoch=$((10#$now_utc))
deployment_grace=$((10#$deployment_grace_minutes))
publication_grace=$((10#$publication_grace_minutes))
if ((now_epoch > 253402300799 ||
     deployment_grace < 1 || deployment_grace > 1440 ||
     publication_grace < 1 || publication_grace > 1440)) ||
   [[ "$(stat -c '%s' "$health_file")" -gt 16384 ||
      "$(stat -c '%s' "$update_file")" -gt 4096 ||
      "$(stat -c '%s' "$release_evidence_file")" -gt 4096 ||
      "$(stat -c '%s' "$run_evidence_file")" -gt 4096 ]]; then
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

invalid_evidence() {
    finish failed 'dev publication evidence was invalid' 1
}

utc_filter='
  type == "string" and
  test("^[0-9]{4}-(0[1-9]|1[0-2])-([0-2][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]Z$") and
  (fromdateiso8601? | type == "number")
'

if ! jq -e '
    type == "object" and
    (keys | sort) == ["assetName", "digest", "publishedUtc", "size", "tagName"] and
    .tagName == "dev" and
    .assetName == "update.json" and
    (.size | type == "number" and . == floor and . > 0 and . <= 4096) and
    (.digest | type == "string" and test("^sha256:[0-9a-f]{64}$")) and
    (.publishedUtc | '"$utc_filter"')
  ' "$release_evidence_file" >/dev/null 2>&1; then
    invalid_evidence
fi

if ! jq -e '
    type == "object" and
    (keys | sort) == ["asset", "branch", "builtUtc", "commit", "run", "sha256", "version"] and
    (.version | type == "string" and length >= 5 and length <= 64 and
      test("^[0-9]+\\.[0-9]+\\.[0-9]+([.-][0-9A-Za-z.-]+)?$")) and
    (.commit | type == "string" and test("^[0-9a-f]{40}$")) and
    (.builtUtc | '"$utc_filter"') and
    (.asset | type == "string" and length >= 1 and length <= 128 and
      test("^[A-Za-z0-9][A-Za-z0-9._+-]*$")) and
    (.sha256 | type == "string" and test("^[0-9a-f]{64}$")) and
    (.branch | type == "string" and length >= 1 and length <= 128 and
      test("^[A-Za-z0-9][A-Za-z0-9._/-]*$")) and
    (.run | type == "string" and test("^[1-9][0-9]{0,15}$") and
      ((tonumber? // 0) <= 9007199254740991))
  ' "$update_file" >/dev/null 2>&1; then
    invalid_evidence
fi

if ! jq -e '
    type == "object" and
    (keys | sort) == [
      "conclusion", "event", "headBranch", "headSha", "id", "runCompletedUtc",
      "runStartedUtc", "status", "workflowPath"
    ] and
    (.id | type == "number" and . == floor and . > 0 and . <= 9007199254740991) and
    (.headSha | type == "string" and test("^[0-9a-f]{40}$")) and
    (.headBranch | type == "string" and length >= 1 and length <= 128) and
    .event == "push" and
    .status == "completed" and
    .conclusion == "success" and
    .workflowPath == ".github/workflows/windows-verify.yml" and
    (.runStartedUtc | '"$utc_filter"') and
    (.runCompletedUtc | '"$utc_filter"')
  ' "$run_evidence_file" >/dev/null 2>&1; then
    invalid_evidence
fi

actual_size="$(stat -c '%s' "$update_file")"
recorded_size="$(jq -r '.size' "$release_evidence_file")"
actual_digest="$(sha256sum "$update_file" | awk '{print $1}')"
recorded_digest="$(jq -r '.digest | sub("^sha256:"; "")' "$release_evidence_file")"
expected_commit="$(jq -r '.commit' "$update_file")"
manifest_branch="$(jq -r '.branch' "$update_file")"
manifest_run="$(jq -r '.run' "$update_file")"
run_id="$(jq -r '.id | tostring' "$run_evidence_file")"
run_commit="$(jq -r '.headSha' "$run_evidence_file")"
run_branch="$(jq -r '.headBranch' "$run_evidence_file")"
published_utc="$(jq -r '.publishedUtc | fromdateiso8601' "$release_evidence_file")"
built_utc="$(jq -r '.builtUtc | fromdateiso8601' "$update_file")"
run_started_utc="$(jq -r '.runStartedUtc | fromdateiso8601' "$run_evidence_file")"
run_completed_utc="$(jq -r '.runCompletedUtc | fromdateiso8601' "$run_evidence_file")"

if [[ "$actual_size" != "$recorded_size" || "$actual_digest" != "$recorded_digest" ||
      "$manifest_run" != "$run_id" || "$expected_commit" != "$run_commit" ||
      "$run_started_utc" -gt "$built_utc" || "$built_utc" -gt "$published_utc" ||
      "$published_utc" -gt "$run_completed_utc" || "$run_completed_utc" -gt "$now_epoch" ]]; then
    invalid_evidence
fi

if [[ "$manifest_branch" != "$default_branch" || "$run_branch" != "$default_branch" ]]; then
    finish non-main-publication 'verified dev publication was not produced from the default branch' 1
fi

if ! resolved_default="$(git -C "$repository_root" rev-parse --verify "$default_branch_commit^{commit}" 2>/dev/null)" ||
   ! resolved_expected="$(git -C "$repository_root" rev-parse --verify "$expected_commit^{commit}" 2>/dev/null)"; then
    invalid_evidence
fi

if [[ "$resolved_default" != "${default_branch_commit,,}" ]] ||
   ! git -C "$repository_root" merge-base --is-ancestor "$resolved_expected" "$resolved_default"; then
    finish non-main-publication 'verified dev publication was not on the default-branch lineage' 1
fi

if ! jq -e '
    type == "object" and
    .status == "ok" and
    (.protocol | type == "number" and . == floor and . >= 1 and . <= 2147483647) and
    (.version | type == "string" and length >= 1 and length <= 96) and
    (.commit | type == "string" and test("^[0-9a-fA-F]{7,40}$"))
  ' "$health_file" >/dev/null 2>&1; then
    finish failed 'relay health response was invalid' 1
fi

actual_commit="$(jq -r '.commit' "$health_file")"
if ! resolved_actual="$(git -C "$repository_root" rev-parse --verify "$actual_commit^{commit}" 2>/dev/null)"; then
    finish failed 'relay health named a commit unavailable in repository history' 1
fi

deployment_grace_seconds=$((deployment_grace * 60))
publication_grace_seconds=$((publication_grace * 60))
publication_age_seconds=$((now_epoch - published_utc))

if [[ "$resolved_actual" == "$resolved_expected" ]]; then
    if [[ "$resolved_default" == "$resolved_expected" ]]; then
        finish ready 'relay matches the latest verified dev publication' 0
    fi

    default_commit_utc="$(git -C "$repository_root" show -s --format=%ct "$resolved_default")"
    default_age_seconds=$((now_epoch - default_commit_utc))
    if ((default_age_seconds < 0)); then
        invalid_evidence
    elif ((default_age_seconds <= publication_grace_seconds)); then
        finish publication-delayed 'default-branch verification and publication are within the expected window' 0
    else
        finish publication-failed 'latest default-branch commit did not reach the verified dev feed within the expected window' 1
    fi
fi

if git -C "$repository_root" merge-base --is-ancestor "$resolved_actual" "$resolved_expected"; then
    if ((publication_age_seconds <= deployment_grace_seconds)); then
        finish updater-delayed 'relay updater is within its scheduled deployment window' 0
    else
        finish updater-stale 'relay updater missed the verified publication window' 1
    fi
fi

if git -C "$repository_root" merge-base --is-ancestor "$resolved_expected" "$resolved_actual"; then
    finish relay-ahead 'relay is ahead of the latest verified dev publication' 1
else
    finish relay-diverged 'relay deployment is not on the verified default-branch lineage' 1
fi
