#!/usr/bin/env bash
# Fails on a user-facing label that has grown into a paragraph.
#
# Reported with a screenshot of the History page carrying one sentence and a truncated copy of
# the same sentence: the header chip was built as $"SQLite · {Status}", so it was the body text
# with a prefix, then ellipsised because it did not fit.
#
# The sweep that found the rest of it measured nineteen XAML labels over eighty characters, the
# longest at 332, and fifteen of the nineteen were on the four pages rewritten the week before
# to fill panels that read as empty. Filling an empty panel with prose is not the same as making
# it useful, and "be concise" is not a thing a review can check twice in a row.
#
# So the budget is a number and this is a ratchet, like sweep-unread.sh beside it. A label is a
# line. Anything longer is either several labels pretending to be one, or documentation that
# belongs in docs/.
#
# Not everything long is prose. An interpolated string is a format, not a sentence, and SVG path
# data is neither -- both are skipped rather than allowlisted, because listing them would be a
# list nobody could read for the entries that matter.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
budget="${PROSE_BUDGET:-120}"
allow="$root/scripts/sweep-prose.allow"

python3 - "$root" "$budget" "$allow" <<'PY'
import pathlib, re, sys

root, budget, allow_path = pathlib.Path(sys.argv[1]), int(sys.argv[2]), pathlib.Path(sys.argv[3])

allowed = set()
if allow_path.exists():
    for line in allow_path.read_text().splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            allowed.add(line.split(None, 1)[0])

def skip(text):
    # A format string is not a sentence, and vector path data is not language.
    return "{" in text or re.match(r"^[Mm]\s*-?\d", text) is not None

found = []

for path in sorted((root / "src/TarkovCompanion.App/Views").rglob("*.axaml")):
    for match in re.finditer(r'(?:Text|Content|ToolTip\.Tip)="([^"{][^"]*)"', path.read_text()):
        text = match.group(1)
        if len(text) > budget and not skip(text):
            found.append((path.relative_to(root), len(text), text))

for path in sorted((root / "src/TarkovCompanion.App/ViewModels").rglob("*.cs")):
    for match in re.finditer(r'"([A-Z][^"\\]*)"', path.read_text()):
        text = match.group(1)
        if len(text) > budget and not skip(text):
            found.append((path.relative_to(root), len(text), text))

over = [(p, n, t) for p, n, t in found if t[:60] not in allowed]

print(f"User-facing labels longer than {budget} characters.")
print()
if not over:
    print(f"OK: every label is within {budget} characters.")
    sys.exit(0)

print("FAIL: these read as paragraphs rather than labels:")
for path, length, text in sorted(over, key=lambda row: -row[1]):
    print(f"  {path}  [{length}]")
    print(f"    {text[:100]}{'...' if len(text) > 100 else ''}")
print()
print("Shorten it, split it into its own labels, or move it to docs/. If it genuinely has to")
print("stay, add its first sixty characters to scripts/sweep-prose.allow with the reason.")
sys.exit(1)
PY
