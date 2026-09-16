#!/usr/bin/env bash
set -euo pipefail

readonly TASK_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly TASK_TEMP="$(mktemp -d "${TMPDIR:-/tmp}/tarkov-relay-watch.XXXXXX")"
trap 'rm -rf -- "$TASK_TEMP"' EXIT

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
readonly MAIN_COMMIT="$(commit_at '2027-01-15T08:16:40Z' main)"
git -C "$repository" switch -q -c non-main "$OLDER_COMMIT"
readonly NON_MAIN_COMMIT="$(commit_at '2027-01-15T08:10:00Z' non-main)"
git -C "$repository" switch -q main

assert_classifier() {
    local name="$1"
    local actual_commit="$2"
    local default_commit="$3"
    local expected_status="$4"
    local expected_reason="$5"
    local expected_exit="$6"
    local directory="${TASK_TEMP}/classify-${name}"
    local output="${directory}/output.txt"

    mkdir -p "$directory"
    jq -n --arg commit "$actual_commit" \
      '{
        status: "ok",
        protocol: 1,
        version: "1.0.700",
        commit: $commit,
        startedUtc: "2027-01-15T08:00:00Z",
        rooms: 2,
        members: 3
      }' \
      > "${directory}/health.json"

    set +e
    bash "${TASK_ROOT}/relay-watch.sh" \
        --health-file "${directory}/health.json" \
        --repository-root "$repository" \
        --default-branch-commit "$default_commit" \
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

assert_classifier current "$MAIN_COMMIT" "$MAIN_COMMIT" ready \
    'relay is live on a known default-branch build' 0
assert_classifier older "$OLDER_COMMIT" "$MAIN_COMMIT" ready \
    'relay is live on a known default-branch build' 0
assert_classifier short-sha "${OLDER_COMMIT:0:7}" "$MAIN_COMMIT" ready \
    'relay is live on a known default-branch build' 0
assert_classifier invalid-health 'not-a-sha' "$MAIN_COMMIT" failed \
    'relay health response was invalid' 1
assert_classifier unknown-health 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' "$MAIN_COMMIT" failed \
    'relay health named a commit unavailable in repository history' 1
assert_classifier invalid-default "$MAIN_COMMIT" 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' failed \
    'default-branch repository evidence was invalid' 1
assert_classifier relay-ahead "$MAIN_COMMIT" "$OLDER_COMMIT" relay-ahead \
    'relay names a known commit ahead of the checked-out default branch' 1
assert_classifier relay-diverged "$NON_MAIN_COMMIT" "$MAIN_COMMIT" relay-diverged \
    'relay deployment is not on the checked-out default-branch lineage' 1

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
fixture_workflow="${TASK_ROOT}/../../../.github/workflows/relay-watch-fixture.yml"
grep -Fq 'group: relay-watch-live' "$workflow"
grep -Fq 'if: github.ref_name == github.event.repository.default_branch' "$workflow"
grep -Fq 'actions/checkout@fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09 # v5.1.0' "$workflow"
grep -Fq 'pull_request:' "$fixture_workflow"
grep -Fq 'group: relay-watch-fixture-pr-${{ github.event.pull_request.number }}' "$fixture_workflow"
grep -Fq 'actions/checkout@fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09 # v5.1.0' "$fixture_workflow"
grep -Fq 'Signed private-ring selection remains the relay updater' "$workflow"
grep -Fq -- '--max-filesize 65536' "$workflow"
grep -Fq -- '--existing-state all' "${TASK_ROOT}/file-relay-reports.sh"
if grep -Fq 'issues: write' "$fixture_workflow"; then
    printf 'relay-watch pull-request fixture must not receive issue-writing permission\n' >&2
    exit 1
fi
if grep -Eq 'releases/tags/dev|release-asset digest|update\.json' "$workflow"; then
    printf 'relay-watch workflow must not treat the retired public dev release as authority\n' >&2
    exit 1
fi
if grep -Eq 'reports.*\|\|[[:space:]]*(echo|printf).*\[\]' "$workflow"; then
    printf 'relay-watch workflow must fail visibly rather than substitute an empty report list\n' >&2
    exit 1
fi

printf 'relay-watch liveness, lineage, report-schema, and exact-issue fixtures passed\n'
