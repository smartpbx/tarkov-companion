#!/usr/bin/env bash
set -euo pipefail

# Every release fixture: policy, manifest, feed, gates, workflow policy, offline recovery and
# the relay updater, plus syntax and lint for the shell they drive. Light enough for the dev
# host; GitHub Actions runs it with TARKOV_RELEASE_REQUIRE_TOOLS=1 so a missing pwsh, shell
# linter or YAML parser fails there instead of quietly skipping what they check.
TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly TASK_PROJECT_ROOT
readonly TASK_SCRIPTS=(
    "${TASK_PROJECT_ROOT}/scripts/release/install-cosign.sh"
    "${TASK_PROJECT_ROOT}/scripts/release/pinned-cosign.sh"
    "${TASK_PROJECT_ROOT}/scripts/release/sign-files.sh"
    "${TASK_PROJECT_ROOT}/scripts/release/verify-signed-file.sh"
    "${TASK_PROJECT_ROOT}/scripts/release/verify-envelope.sh"
    "${TASK_PROJECT_ROOT}/scripts/release/verify-offline.sh"
    "${TASK_PROJECT_ROOT}/scripts/release/verify-relay-package.sh"
    "${TASK_PROJECT_ROOT}/scripts/release/install-syft.sh"
    "${TASK_PROJECT_ROOT}/scripts/release/transition.sh"
    "${TASK_PROJECT_ROOT}/scripts/release/test-real-sigstore.sh"
    "${TASK_PROJECT_ROOT}/scripts/release/test.sh"
    "${TASK_PROJECT_ROOT}/deploy/group-server/tarkov-group-update.sh"
)

# One file at a time: `bash -n a b c` parses a and then runs b and c as its arguments.
for script in "${TASK_SCRIPTS[@]}"; do
    bash -n "${script}"
done
if command -v shellcheck >/dev/null 2>&1; then
    shellcheck -x "${TASK_SCRIPTS[@]}"
elif [[ "${TARKOV_RELEASE_REQUIRE_TOOLS:-0}" == "1" ]]; then
    printf 'shellcheck is required here but is not installed\n' >&2
    exit 1
else
    printf 'shellcheck is not installed; lint skipped on this host\n'
fi

PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover \
    --start-directory "${TASK_PROJECT_ROOT}/scripts/release/tests" \
    --pattern 'test_*.py' \
    --verbose
