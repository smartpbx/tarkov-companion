#!/usr/bin/env bash
set -euo pipefail

readonly TASK_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly TASK_TEMP="$(mktemp -d "${TMPDIR:-/tmp}/tarkov-relay-watch.XXXXXX")"
trap 'rm -rf -- "${TASK_TEMP}"' EXIT
readonly EXPECTED_COMMIT="0123456789abcdef0123456789abcdef01234567"

assert_classifier() {
    local name="$1"
    local fixture="$2"
    local expected_status="$3"
    local expected_reason="$4"
    local expected_exit="$5"
    local expected_commit_utc="$6"
    local now_utc="$7"
    local output="${TASK_TEMP}/${name}.txt"

    set +e
    bash "${TASK_ROOT}/relay-watch.sh" \
        --health-file "${TASK_ROOT}/fixtures/${fixture}" \
        --expected-commit "${EXPECTED_COMMIT}" \
        --expected-commit-utc "$expected_commit_utc" \
        --now-utc "$now_utc" \
        --deployment-grace-minutes 45 \
        >"$output" 2>&1
    local actual_exit=$?
    set -e

    if [[ "$actual_exit" != "$expected_exit" ]] ||
       ! grep -Fxq "status=${expected_status}" "$output" ||
       ! grep -Fxq "reason=${expected_reason}" "$output"; then
        printf 'relay-watch %s fixture failed\n' "$name" >&2
        sed -n '1,80p' "$output" >&2
        exit 1
    fi
}

# A matching relay stays healthy when main has been quiet for longer than the update grace.
assert_classifier ready ready-health.json ready 'relay health is current' 0 1800000000 1800005461
# The relay can report Git's abbreviated SHA without being classified as a mismatch.
assert_classifier short-sha short-sha-health.json ready 'relay health is current' 0 1800000000 1800005461
# A different known build is tolerated only while publication plus the 30±5 minute poll can run.
assert_classifier mismatch mismatch-health.json updating 'relay deployment is within the documented grace window' 0 1800000000 1800001200
# After that documented window, the same mismatch is a real stale-deployment incident.
assert_classifier stale mismatch-health.json stale 'relay did not reach the expected deployment before the grace window elapsed' 1 1800000000 1800002701
# Commit age alone is not a deployment fault when the reported commit is still current.
assert_classifier age stale-health.json ready 'relay health is current' 0 1800000000 1800005461
assert_classifier invalid-body invalid-health.json failed 'relay health response was invalid' 1 1800000000 1800001200

fake_bin="${TASK_TEMP}/bin"
mkdir -p "$fake_bin"
cat >"${fake_bin}/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$GH_CALL_LOG"
if [[ "$1 $2" == 'issue list' ]]; then
    cat "$GH_LIST_RESPONSE"
fi
EOF
chmod +x "${fake_bin}/gh"
printf '%s\n' 'safe fixture body' >"${TASK_TEMP}/body.md"

# The same title comments on the one open incident rather than creating a second hourly issue.
: >"${TASK_TEMP}/calls-open.txt"
PATH="${fake_bin}:$PATH" GH_CALL_LOG="${TASK_TEMP}/calls-open.txt" \
GH_LIST_RESPONSE="${TASK_ROOT}/fixtures/issue-open.txt" \
bash "${TASK_ROOT}/open-relay-issue.sh" --repository owner/repo \
    --title 'Relay watch: the group relay needs looking at' --body-file "${TASK_TEMP}/body.md"
grep -Fqx 'issue comment 42 --repo owner/repo --body-file '"${TASK_TEMP}"'/body.md' "${TASK_TEMP}/calls-open.txt"

# A report reference is already represented by its open issue, so its duplicate control path is
# intentionally silent rather than adding the same body every hour.
: >"${TASK_TEMP}/calls-skip.txt"
PATH="${fake_bin}:$PATH" GH_CALL_LOG="${TASK_TEMP}/calls-skip.txt" \
GH_LIST_RESPONSE="${TASK_ROOT}/fixtures/issue-open.txt" \
bash "${TASK_ROOT}/open-relay-issue.sh" --repository owner/repo \
    --title 'Problem report fixture-reference' --body-file "${TASK_TEMP}/body.md" --on-existing skip
if [[ "$(wc -l <"${TASK_TEMP}/calls-skip.txt")" != 1 ]]; then
    printf 'relay-watch issue skip fixture failed\n' >&2
    exit 1
fi

# With no existing issue, the control path creates exactly one issue with the supplied safe body.
: >"${TASK_TEMP}/calls-none.txt"
PATH="${fake_bin}:$PATH" GH_CALL_LOG="${TASK_TEMP}/calls-none.txt" \
GH_LIST_RESPONSE="${TASK_ROOT}/fixtures/issue-none.txt" \
bash "${TASK_ROOT}/open-relay-issue.sh" --repository owner/repo \
    --title 'Relay watch: the group relay needs looking at' --body-file "${TASK_TEMP}/body.md"
grep -Fqx 'issue create --repo owner/repo --title Relay watch: the group relay needs looking at --body-file '"${TASK_TEMP}"'/body.md' "${TASK_TEMP}/calls-none.txt"

printf 'relay-watch ready, mismatch, age, invalid-body, and issue-dedup fixtures passed\n'
