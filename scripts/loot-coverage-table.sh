#!/usr/bin/env bash
# Regenerates the loot coverage table in docs/MAPS.md from a real loot-spawn publication cache.
#
# Usage: scripts/loot-coverage-table.sh <publication.cache> [seed catalog .db]
#
# The table is what the app itself shows: the preview tool opens every map in the Raid page with
# the high-value loot layer on and writes one row per map (see LootCoverageTable.cs). It replaces
# the lines between the two loot-coverage markers in docs/MAPS.md and nothing else.
# Build tools/V2RenderPreview (Release) first; run it on the dev host, never the workstation.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cache="${1:?usage: $0 <publication.cache> [seed catalog .db]}"
seed="${2:-/root/orca/seed/catalog-2026-09-14.db}"
preview="$root/tools/V2RenderPreview/bin/Release/net10.0/TarkovCompanion.V2RenderPreview.dll"
doc="$root/docs/MAPS.md"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

[[ -f "$preview" ]] || { echo "Build tools/V2RenderPreview in Release first." >&2; exit 1; }

dotnet "$preview" --ui-shell v2-a --route raid --map customs --raid-demo \
    --width 1920 --height 1080 --out "$work/frame.png" \
    --seed-database "$seed" --seed-loot-cache "$cache" \
    --loot-coverage-table "$work/table.md" > "$work/preview.log" 2>&1 ||
    { cat "$work/preview.log" >&2; exit 1; }
[[ -s "$work/table.md" ]] || { cat "$work/preview.log" >&2; echo "No table written." >&2; exit 1; }

start='<!-- loot-coverage:start -->'
end='<!-- loot-coverage:end -->'
grep -qF "$start" "$doc" && grep -qF "$end" "$doc" || { echo "Markers missing from $doc." >&2; exit 1; }
awk -v start="$start" -v end="$end" -v table="$work/table.md" '
    $0 == start { print; while ((getline line < table) > 0) print line; skip = 1; next }
    $0 == end { skip = 0 }
    !skip { print }
' "$doc" > "$work/MAPS.md"
mv "$work/MAPS.md" "$doc"
echo "Updated $doc"
