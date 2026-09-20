#!/usr/bin/env bash
set -euo pipefail

# The version of a build, decided here and nowhere else.
#
#   scripts/build-version.sh 1140   ->  2.0.1140
#   scripts/build-version.sh        ->  2.0.0-dev
#
# PRODUCT_VERSION at the repository root holds the first two parts: the product generation
# (2 is the V2 workspace) and a minor nothing has earned yet. The last part is the CI run that
# built it, which is the only part that has ever named a particular build.
#
# This exists because "1.0" was typed out twelve times, nine in the Windows workflow and three
# in the packaging script, so it never moved, and an installed V2 build introduced itself as
# 1.0.1121. Everything that needs the version now reads it from
# here: the workflow, the package and its zip name, the relay's identity, the installer and
# its feed, and the verification that checks they all agree. Directory.Build.props reads the
# same file, so a local build that nobody stamped says 2.0.0-dev rather than the SDK's 1.0.0.
#
# With no build number this is a development build and says so, which at least does not claim
# to be one a run produced.
TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly TASK_PROJECT_ROOT
# tr, because a checkout on Windows may hand this file over with a carriage return in it.
TASK_PRODUCT="$(tr -d '[:space:]' < "${TASK_PROJECT_ROOT}/PRODUCT_VERSION")"
readonly TASK_PRODUCT
if [[ ! "${TASK_PRODUCT}" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
    printf 'PRODUCT_VERSION must be MAJOR.MINOR, and it says: %s\n' "${TASK_PRODUCT}" >&2
    exit 1
fi

if [[ $# -eq 0 ]]; then
    printf '%s.0-dev\n' "${TASK_PRODUCT}"
    exit 0
fi

readonly TASK_BUILD="$1"
# 65534 is the largest number an assembly version part can hold, and the build number goes
# into one. Better refused here than as a compiler error twenty minutes into a run.
if [[ ! "${TASK_BUILD}" =~ ^(0|[1-9][0-9]{0,4})$ ]] || (( TASK_BUILD > 65534 )); then
    printf 'The build number must be a whole number up to 65534, and it is: %s\n' "${TASK_BUILD}" >&2
    exit 1
fi

printf '%s.%s\n' "${TASK_PRODUCT}" "${TASK_BUILD}"
