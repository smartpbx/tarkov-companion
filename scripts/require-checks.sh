#!/usr/bin/env bash
# The status checks a pull request must pass before `main` will take it.
#
# Branch protection is a repository setting, so nothing in this repository can enforce it and
# no test can see it. What this repository can hold is the list itself, and a command that
# compares GitHub's answer with the list and says which way they differ.
#
# The list is short on purpose. `windows-verify` — the job that launches the packaged
# application, opens every page and photographs it — was dropped on 2026-09-17, restored on
# 2026-09-19 under #279, and dropped again on 2026-09-20. What the restoration got right is that
# a package which opens no window must not reach anybody; what it got wrong is where to stop it.
# As a merge gate, with branches required to be current, it cost one fifteen-to-thirty-minute
# Windows run per pull request per merge: twenty-seven open pull requests, each invalidated by
# every other one landing, and an hour to ship a one-line fix.
#
# The gate now sits where the harm is. windows-verify.yml runs on every push to `main`, and
# publish.yml accepts nothing but a successful push-to-main run, so a build that fails it is
# still never published. `main` can be red for a run; nothing anybody installs can be.
#
#   scripts/require-checks.sh            reports drift, exits non-zero if the set differs
#   scripts/require-checks.sh --apply    writes the set (needs a token with administration write)
set -euo pipefail

REPOSITORY="${TARKOV_REPOSITORY:-smartpbx/tarkov-companion}"
BRANCH="${TARKOV_PROTECTED_BRANCH:-main}"

# linux — ci.yml: build, the whole test suite, and the safety/secret/licence audits.
REQUIRED=(linux)

apply=0
[[ "${1:-}" == "--apply" ]] && apply=1

endpoint="repos/$REPOSITORY/branches/$BRANCH/protection/required_status_checks"

if (( apply )); then
  body="$(printf '%s\n' "${REQUIRED[@]}" | jq -R . | jq -sc '{strict: false, checks: map({context: .})}')"
  printf '%s' "$body" | gh api --method PATCH "$endpoint" --input - >/dev/null
fi

observed="$(gh api "$endpoint" --jq '.contexts[]' | sort)"
expected="$(printf '%s\n' "${REQUIRED[@]}" | sort)"
strict="$(gh api "$endpoint" --jq '.strict')"

if [[ "$observed" != "$expected" ]]; then
  echo "Required checks on $REPOSITORY@$BRANCH do not match this repository's list." >&2
  diff <(echo "$expected") <(echo "$observed") | sed 's/^</wanted: /; s/^>/present: /' >&2
  exit 1
fi

if [[ "$strict" != "false" ]]; then
  echo "A branch must be up to date with $BRANCH before it can merge; see CONTRIBUTING.md for why that was removed." >&2
  exit 1
fi

echo "$REPOSITORY@$BRANCH requires ${REQUIRED[*]}; Windows verification gates publishing, not merging."
