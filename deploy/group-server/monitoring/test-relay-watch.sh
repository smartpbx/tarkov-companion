#!/usr/bin/env bash
set -euo pipefail

readonly TASK_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly TASK_TEMP="$(mktemp -d "${TMPDIR:-/tmp}/tarkov-relay-watch.XXXXXX")"
trap 'rm -rf -- "$TASK_TEMP"' EXIT

readonly BUILD_UTC='2027-01-15T07:55:00Z'
readonly RUN_STARTED_UTC='2027-01-15T07:50:00Z'
readonly RUN_COMPLETED_UTC='2027-01-15T08:01:00Z'

repository="${TASK_TEMP}/repository"
git init -q --initial-branch=main "$repository"
git -C "$repository" config user.name 'Relay fixture'
git -C "$repository" config user.email 'relay-fixture@example.invalid'

commit_at() {
    local timestamp="$1"
    local message="$2"
    GIT_AUTHOR_DATE="$timestamp" GIT_COMMITTER_DATE="$timestamp" \
        git -C "$repository" commit -q --allow-empty -m "$message"
    git -C "$repository" rev-parse HEAD
}

readonly OLDER_COMMIT="$(commit_at '2027-01-15T05:13:20Z' older)"
readonly PUBLISHED_COMMIT="$(commit_at '2027-01-15T07:30:00Z' published)"
readonly MAIN_COMMIT="$(commit_at '2027-01-15T08:16:40Z' main)"
git -C "$repository" switch -q -c non-main "$OLDER_COMMIT"
readonly NON_MAIN_COMMIT="$(commit_at '2027-01-15T08:10:00Z' non-main)"
git -C "$repository" switch -q main

write_evidence() {
    local directory="$1"
    local expected_commit="$2"
    local branch="$3"
    local run_conclusion="$4"

    mkdir -p "$directory"
    jq -n \
      --arg commit "$expected_commit" \
      --arg built "$BUILD_UTC" \
      --arg branch "$branch" \
      '{
        version: "1.0.700",
        commit: $commit,
        builtUtc: $built,
        asset: "TarkovCompanion-v1.0.0-win-x64.zip",
        sha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        branch: $branch,
        run: "12345"
      }' > "${directory}/update.json"

    local digest
    local size
    digest="$(sha256sum "${directory}/update.json" | awk '{print $1}')"
    size="$(stat -c '%s' "${directory}/update.json")"
    jq -n \
      --arg digest "sha256:${digest}" \
      --argjson size "$size" \
      '{
        tagName: "dev",
        assetName: "update.json",
        size: $size,
        digest: $digest,
        publishedUtc: "2027-01-15T08:00:00Z"
      }' > "${directory}/release.json"

    jq -n \
      --arg commit "$expected_commit" \
      --arg branch "$branch" \
      --arg conclusion "$run_conclusion" \
      --arg started "$RUN_STARTED_UTC" \
      --arg completed "$RUN_COMPLETED_UTC" \
      '{
        id: 12345,
        headSha: $commit,
        headBranch: $branch,
        event: "push",
        status: "completed",
        conclusion: $conclusion,
        workflowPath: ".github/workflows/windows-verify.yml",
        runStartedUtc: $started,
        runCompletedUtc: $completed
      }' > "${directory}/run.json"
}

assert_classifier() {
    local name="$1"
    local actual_commit="$2"
    local expected_commit="$3"
    local default_commit="$4"
    local branch="$5"
    local now_utc="$6"
    local expected_status="$7"
    local expected_reason="$8"
    local expected_exit="$9"
    local conclusion="${10:-success}"
    local directory="${TASK_TEMP}/classify-${name}"
    local output="${directory}/output.txt"

    write_evidence "$directory" "$expected_commit" "$branch" "$conclusion"
    jq -n --arg commit "$actual_commit" \
      '{status: "ok", protocol: 1, version: "1.0.700", commit: $commit}' \
      > "${directory}/health.json"

    set +e
    bash "${TASK_ROOT}/relay-watch.sh" \
        --health-file "${directory}/health.json" \
        --update-file "${directory}/update.json" \
        --release-evidence-file "${directory}/release.json" \
        --run-evidence-file "${directory}/run.json" \
        --repository-root "$repository" \
        --default-branch main \
        --default-branch-commit "$default_commit" \
        --now-utc "$now_utc" \
        --deployment-grace-minutes 45 \
        --publication-grace-minutes 45 \
        > "$output" 2>&1
    local actual_exit=$?
    set -e

    if [[ "$actual_exit" != "$expected_exit" ]] ||
       [[ "$(wc -l < "$output")" != 2 ]] ||
       ! grep -Fxq "status=${expected_status}" "$output" ||
       ! grep -Fxq "reason=${expected_reason}" "$output"; then
        printf 'relay-watch %s fixture failed\n' "$name" >&2
        sed -n '1,80p' "$output" >&2
        exit 1
    fi
}

assert_classifier ready "$PUBLISHED_COMMIT" "$PUBLISHED_COMMIT" "$PUBLISHED_COMMIT" main \
    1800005461 ready 'relay matches the latest verified dev publication' 0
assert_classifier short-sha "${PUBLISHED_COMMIT:0:7}" "$PUBLISHED_COMMIT" "$PUBLISHED_COMMIT" main \
    1800005461 ready 'relay matches the latest verified dev publication' 0
assert_classifier invalid-health 'not-a-sha' "$PUBLISHED_COMMIT" "$PUBLISHED_COMMIT" main \
    1800001200 failed 'relay health response was invalid' 1
assert_classifier unknown-health 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' "$PUBLISHED_COMMIT" "$PUBLISHED_COMMIT" main \
    1800001200 failed 'relay health named a commit unavailable in repository history' 1
assert_classifier updater-delayed "$OLDER_COMMIT" "$PUBLISHED_COMMIT" "$PUBLISHED_COMMIT" main \
    1800001200 updater-delayed 'relay updater is within its scheduled deployment window' 0
assert_classifier updater-stale "$OLDER_COMMIT" "$PUBLISHED_COMMIT" "$PUBLISHED_COMMIT" main \
    1800002701 updater-stale 'relay updater missed the verified publication window' 1
assert_classifier publication-delayed "$PUBLISHED_COMMIT" "$PUBLISHED_COMMIT" "$MAIN_COMMIT" main \
    1800001200 publication-delayed 'default-branch verification and publication are within the expected window' 0
assert_classifier publication-failed "$PUBLISHED_COMMIT" "$PUBLISHED_COMMIT" "$MAIN_COMMIT" main \
    1800004001 publication-failed 'latest default-branch commit did not reach the verified dev feed within the expected window' 1
assert_classifier relay-ahead "$MAIN_COMMIT" "$PUBLISHED_COMMIT" "$MAIN_COMMIT" main \
    1800001200 relay-ahead 'relay is ahead of the latest verified dev publication' 1
assert_classifier relay-diverged "$NON_MAIN_COMMIT" "$PUBLISHED_COMMIT" "$MAIN_COMMIT" main \
    1800001200 relay-diverged 'relay deployment is not on the verified default-branch lineage' 1
assert_classifier non-main-branch "$NON_MAIN_COMMIT" "$NON_MAIN_COMMIT" "$MAIN_COMMIT" feature \
    1800001200 non-main-publication 'verified dev publication was not produced from the default branch' 1
assert_classifier non-main-lineage "$NON_MAIN_COMMIT" "$NON_MAIN_COMMIT" "$MAIN_COMMIT" main \
    1800001200 non-main-publication 'verified dev publication was not on the default-branch lineage' 1
assert_classifier failed-run "$PUBLISHED_COMMIT" "$PUBLISHED_COMMIT" "$PUBLISHED_COMMIT" main \
    1800001200 failed 'dev publication evidence was invalid' 1 failure

tampered="${TASK_TEMP}/classify-tampered"
write_evidence "$tampered" "$PUBLISHED_COMMIT" main success
printf '\n' >> "${tampered}/update.json"
jq -n --arg commit "$PUBLISHED_COMMIT" \
  '{status: "ok", protocol: 1, version: "1.0.700", commit: $commit}' \
  > "${tampered}/health.json"
set +e
bash "${TASK_ROOT}/relay-watch.sh" \
  --health-file "${tampered}/health.json" \
  --update-file "${tampered}/update.json" \
  --release-evidence-file "${tampered}/release.json" \
  --run-evidence-file "${tampered}/run.json" \
  --repository-root "$repository" \
  --default-branch main \
  --default-branch-commit "$PUBLISHED_COMMIT" \
  --now-utc 1800001200 > "${tampered}/output.txt" 2>&1
tampered_exit=$?
set -e
[[ "$tampered_exit" == 1 ]]
grep -Fxq 'status=failed' "${tampered}/output.txt"
grep -Fxq 'reason=dev publication evidence was invalid' "${tampered}/output.txt"

missing_value_output="${TASK_TEMP}/missing-value.txt"
set +e
bash "${TASK_ROOT}/relay-watch.sh" --health-file > "$missing_value_output" 2>&1
missing_value_exit=$?
set -e
[[ "$missing_value_exit" == 2 ]]
grep -Fq 'option requires a value' "$missing_value_output"

fake_bin="${TASK_TEMP}/bin"
mkdir -p "$fake_bin"
cat > "${fake_bin}/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$GH_CALL_LOG"
if [[ "$1 $2" == 'issue list' ]]; then
    cat "$GH_LIST_RESPONSE"
fi
EOF
chmod +x "${fake_bin}/gh"
printf '%s\n' 'safe fixture body' > "${TASK_TEMP}/body.md"

assert_call() {
    local pattern="$1"
    local file="$2"
    if ! grep -Fq -- "$pattern" "$file"; then
        printf 'expected call not found: %s\n' "$pattern" >&2
        sed -n '1,40p' "$file" >&2
        exit 1
    fi
}

: > "${TASK_TEMP}/calls-health-open.txt"
PATH="${fake_bin}:$PATH" GH_CALL_LOG="${TASK_TEMP}/calls-health-open.txt" \
GH_LIST_RESPONSE="${TASK_ROOT}/fixtures/issue-health-open.json" \
bash "${TASK_ROOT}/open-relay-issue.sh" --repository owner/repo \
    --title 'Relay watch: the group relay needs looking at' --body-file "${TASK_TEMP}/body.md"
assert_call 'issue list --repo owner/repo --state open' "${TASK_TEMP}/calls-health-open.txt"
assert_call 'issue comment 42 --repo owner/repo' "${TASK_TEMP}/calls-health-open.txt"

: > "${TASK_TEMP}/calls-similar.txt"
PATH="${fake_bin}:$PATH" GH_CALL_LOG="${TASK_TEMP}/calls-similar.txt" \
GH_LIST_RESPONSE="${TASK_ROOT}/fixtures/issue-similar.json" \
bash "${TASK_ROOT}/open-relay-issue.sh" --repository owner/repo \
    --title 'Relay watch: the group relay needs looking at' --body-file "${TASK_TEMP}/body.md"
assert_call 'issue create --repo owner/repo --title Relay watch: the group relay needs looking at' \
    "${TASK_TEMP}/calls-similar.txt"

: > "${TASK_TEMP}/calls-report-closed.txt"
PATH="${fake_bin}:$PATH" GH_CALL_LOG="${TASK_TEMP}/calls-report-closed.txt" \
GH_LIST_RESPONSE="${TASK_ROOT}/fixtures/issue-report-closed.json" \
bash "${TASK_ROOT}/open-relay-issue.sh" --repository owner/repo \
    --title 'Problem report abcdef123456' --body-file "${TASK_TEMP}/body.md" \
    --on-existing skip --existing-state all --label problem-report
assert_call 'issue list --repo owner/repo --state all' "${TASK_TEMP}/calls-report-closed.txt"
[[ "$(wc -l < "${TASK_TEMP}/calls-report-closed.txt")" == 1 ]]

: > "${TASK_TEMP}/calls-reports-valid.txt"
PATH="${fake_bin}:$PATH" GH_CALL_LOG="${TASK_TEMP}/calls-reports-valid.txt" \
GH_LIST_RESPONSE="${TASK_ROOT}/fixtures/issue-none.json" \
bash "${TASK_ROOT}/file-relay-reports.sh" \
    --list-file "${TASK_ROOT}/fixtures/report-list-valid.json" --repository owner/repo
[[ "$(grep -c '^issue create ' "${TASK_TEMP}/calls-reports-valid.txt")" == 2 ]]
[[ "$(grep -c -- '--state all' "${TASK_TEMP}/calls-reports-valid.txt")" == 2 ]]
[[ "$(grep -c -- '--label problem-report' "${TASK_TEMP}/calls-reports-valid.txt")" == 2 ]]

assert_report_rejected() {
    local name="$1"
    local fixture="$2"
    local calls="${TASK_TEMP}/calls-report-${name}.txt"
    local output="${TASK_TEMP}/report-${name}.txt"
    : > "$calls"
    set +e
    PATH="${fake_bin}:$PATH" GH_CALL_LOG="$calls" \
    GH_LIST_RESPONSE="${TASK_ROOT}/fixtures/issue-none.json" \
    bash "${TASK_ROOT}/file-relay-reports.sh" \
      --list-file "$fixture" --repository owner/repo > "$output" 2>&1
    local result=$?
    set -e
    [[ "$result" == 1 ]]
    [[ ! -s "$calls" ]]
    grep -Eq 'exceeded the public-issue validation limit|failed closed-schema validation' "$output"
}

assert_report_rejected multiline "${TASK_ROOT}/fixtures/report-list-multiline.json"
assert_report_rejected nested "${TASK_ROOT}/fixtures/report-list-nested.json"
assert_report_rejected extra "${TASK_ROOT}/fixtures/report-list-extra.json"
assert_report_rejected duplicate "${TASK_ROOT}/fixtures/report-list-duplicate.json"
# This is the current live /reports shape. Rejecting it is intentional until #310 makes the
# list contract return the same 12-hex reference accepted by the restricted read route.
assert_report_rejected timestamped "${TASK_ROOT}/fixtures/report-list-timestamped.json"

oversized="${TASK_TEMP}/report-list-oversized.json"
head -c 65537 /dev/zero | tr '\0' 'a' > "$oversized"
assert_report_rejected oversized "$oversized"

too_many="${TASK_TEMP}/report-list-too-many.json"
jq -n '[range(0; 51) | {
    reference: "abcdef123456",
    bytes: 10,
    receivedUtc: "2026-09-15T12:00:00Z"
  }]' > "$too_many"
assert_report_rejected too-many "$too_many"

workflow="${TASK_ROOT}/../../../.github/workflows/relay-watch.yml"
grep -Fq "group: relay-watch-\${{ github.event_name == 'pull_request'" "$workflow"
grep -Fq "if: github.event_name != 'pull_request' && github.ref_name == github.event.repository.default_branch" "$workflow"
grep -Fq -- '--max-filesize 65536' "$workflow"
grep -Fq -- '--existing-state all' "${TASK_ROOT}/file-relay-reports.sh"
if grep -Eq 'reports.*\|\|[[:space:]]*(echo|printf).*\[\]' "$workflow"; then
    printf 'relay-watch workflow must fail visibly rather than substitute an empty report list\n' >&2
    exit 1
fi

printf 'relay-watch publication, classification, report-schema, and exact-issue fixtures passed\n'
