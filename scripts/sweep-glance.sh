#!/usr/bin/env bash
# The glance ratchet (#712 0-7): the Now panel never scrolls, every guess on it shows its label,
# and every automatic switch says why. Fails, like sweep-prose.sh; it never warns.
#
# Two halves. The desktop half is ordinary unit tests (NowPanelGlanceTests: every phase and a worst
# case at 1920x1080 and 1920x1009, 100/125/150% text; SwitchBecauseGlanceTests: whole evenings
# through the situation engine). They run in the suite's Test step; this checks the results file
# says they ran and passed, so deleting or skipping one fails here rather than going quiet. The
# tablet half (TabletNowPanelBrowserTests: 1280x800 without scrolling) needs a real Chromium, which
# the Linux runner does not have, so without this step it passed every run having checked nothing.
#
# Usage: scripts/sweep-glance.sh [results.trx]   (CI passes the Test step's trx; without one the
# desktop half is run here). Needs a built Release UnitTests project, node and npm.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tests="$root/tests/TarkovCompanion.UnitTests"
# One per test case, theory rows included. Raise it when a glance test is added; never lower it.
floor=7
playwright_version="1.63.0"
dotnet="${DOTNET:-dotnet}"

trx="${1:-}"
if [[ -z "$trx" ]]; then
    results="$(mktemp -d)"
    "$dotnet" test "$tests" --configuration Release --no-build --no-restore \
        --filter "FullyQualifiedName~GlanceTests" --results-directory "$results" \
        --logger "trx;LogFileName=glance.trx" >/dev/null || true
    trx="$results/glance.trx"
fi

python3 - "$trx" "$floor" <<'PY'
import sys, xml.etree.ElementTree as ET

path, floor = sys.argv[1], int(sys.argv[2])
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
results = [r for r in ET.parse(path).getroot().iterfind(".//t:UnitTestResult", ns) if "GlanceTests." in r.get("testName", "")]
bad = [f"{r.get('outcome')}: {r.get('testName')}" for r in results if r.get("outcome") != "Passed"]
passed = len(results) - len(bad)
print(f"glance: {passed} desktop checks passed (floor {floor})")
for line in bad:
    print(f"  {line}")
if bad or passed < floor:
    sys.exit(f"glance ratchet: the desktop half failed or shrank ({passed} passed, floor {floor}); see NowPanelGlanceTests")
PY

# The tablet half. Playwright goes in a scratch prefix, not the repository.
prefix="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/glance-playwright"
if ! NODE_PATH="$prefix/node_modules" node -e 'require("playwright")' 2>/dev/null; then
    npm install --silent --no-save --no-package-lock --no-audit --no-fund --prefix "$prefix" "playwright@$playwright_version" >/dev/null
fi
"$prefix/node_modules/.bin/playwright" install --only-shell chromium >/dev/null
NODE_PATH="$prefix/node_modules" TARKOV_REQUIRE_REAL_BROWSER=1 \
    "$dotnet" test "$tests" --configuration Release --no-build --no-restore \
    --filter "FullyQualifiedName~TabletNowPanelBrowserTests" --logger "console;verbosity=minimal"
echo "glance: the tablet Now panel fits 1280x800 without scrolling"
