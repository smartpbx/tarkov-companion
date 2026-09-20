#!/usr/bin/env bash
# The status checks a pull request must pass before `main` will take it.
#
# Branch protection is a repository setting, so nothing in this repository can enforce it and
# no test can see it. What this repository can hold is the list itself, and a command that
# compares GitHub's answer with the list and says which way they differ.
#
# It matters because the list has already been wrong. `windows-verify` — the job that launches
# the packaged application, opens every page and photographs it — was dropped from the required
# set on 2026-09-17 to stop a slow run blocking merges. The harness kept running and kept
# finding faults ("the app does not start", three times in one day), and none of them could fail
# a merge. The gate was restored on 2026-09-19 under #279.
#
#   scripts/require-checks.sh            reports drift, exits non-zero if the set differs
#   scripts/require-checks.sh --apply    writes the set (needs a token with administration write)
#
# `windows-build` is deliberately NOT here. It packages and runs the whole suite on Windows a
# second time; `checks` and `windows-verify` already cover those on the branch, and requiring a
# fourth long job buys nothing that `windows-verify` does not already refuse to be green without.
set -euo pipefail

REPOSITORY="${TARKOV_REPOSITORY:-smartpbx/tarkov-companion}"
BRANCH="${TARKOV_PROTECTED_BRANCH:-main}"

# linux         — ci.yml: build, test and the safety/secret/licence audits.
# checks        — windows-verify.yml: the same suite on the runner that then packages it.
# windows-verify — windows-verify.yml: launches the package and photographs every page.
REQUIRED=(linux checks windows-verify)

apply=0
[[ "${1:-}" == "--apply" ]] && apply=1

endpoint="repos/$REPOSITORY/branches/$BRANCH/protection/required_status_checks"

if (( apply )); then
  body="$(printf '%s\n' "${REQUIRED[@]}" | jq -R . | jq -sc '{strict: true, checks: map({context: .})}')"
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

if [[ "$strict" != "true" ]]; then
  echo "A branch may merge without being up to date with $BRANCH; see CONTRIBUTING.md." >&2
  exit 1
fi

echo "$REPOSITORY@$BRANCH requires ${REQUIRED[*]}, and a branch must be current with it."
