#!/usr/bin/env bash
set -euo pipefail

readonly TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly TASK_TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/tarkov-license-test.XXXXXX")"
trap 'rm -rf -- "${TASK_TEMP_DIR}"' EXIT

if "${TASK_PROJECT_ROOT}/scripts/audit-licenses.sh" \
    --graph "${TASK_PROJECT_ROOT}/fixtures/licenses/missing-mapping.graph.json" \
    --mapping "${TASK_PROJECT_ROOT}/fixtures/licenses/missing-mapping.map.json" \
    --output "${TASK_TEMP_DIR}/inventory.json" \
    >"${TASK_TEMP_DIR}/stdout.txt" 2>"${TASK_TEMP_DIR}/stderr.txt"; then
    printf 'License audit fixture failed: an unmapped shipped package was accepted\n' >&2
    exit 1
fi

if ! rg --fixed-strings --quiet \
    'License audit failed: missing license mapping: Fixture.Runtime/1.0.0' \
    "${TASK_TEMP_DIR}/stderr.txt"; then
    printf 'License audit fixture failed: expected missing-mapping diagnostic was not emitted\n' >&2
    sed -n '1,120p' "${TASK_TEMP_DIR}/stderr.txt" >&2
    exit 1
fi

printf 'License audit missing-mapping fixture passed\n'
