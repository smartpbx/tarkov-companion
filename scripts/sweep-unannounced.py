#!/usr/bin/env python3
"""Reports computed properties that nothing ever announces.

A view model property with no backing field is evaluated when the view binds and then never
again unless somebody raises a change for it by name. Miss that and the feature is built,
bound, and permanently showing whatever was true at startup.

Found the honest way: "Stack the floors" shipped, was bound to CanStack, and was invisible on
every map, because CanStack reads Floors.Count and nothing raised it when the floors arrived.
The page-gallery screenshot caught it, and only because somebody looked.

Only properties declared inside a `class` are considered. A `record` is replaced wholesale
rather than mutated, so its computed properties cannot go stale, and including them buried the
real finding under sixty that were fine.

Reports rather than fails, for the same reason the schema sweep does: a property set once in a
constructor is legitimately never announced, and a gate here would teach people to route around
it.
"""

from __future__ import annotations

import pathlib
import re
import sys

TYPE = re.compile(r"^\s*(?:public|internal|private|protected)?\s*(?:sealed\s+|abstract\s+|static\s+|partial\s+)*(class|record|interface|enum|struct)\s+([A-Za-z0-9_]+)")
COMPUTED = re.compile(r"^\s*public\s+(?:bool|string|double|int|Matrix|Thickness)\??\s+([A-Z][A-Za-z0-9]*)\s*=>(.*)$")
# A member reference: a backing field, or another property read off this or another object.
DEPENDS = re.compile(r"_[a-z][A-Za-z0-9]*|\b[A-Z][A-Za-z0-9]*\.[A-Z]|\b[A-Z][A-Za-z0-9]*\s*(?:\.|\?\.)")


def sweep(root: pathlib.Path) -> list[tuple[str, str]]:
    suspects: list[tuple[str, str]] = []
    for path in sorted(root.glob("src/**/*.cs")):
        text = path.read_text(encoding="utf-8", errors="replace")
        if "OnPropertyChanged" not in text:
            continue

        kind = None
        for line in text.splitlines():
            declaration = TYPE.match(line)
            if declaration:
                kind = declaration.group(1)
                continue

            if kind != "class":
                continue

            computed = COMPUTED.match(line)
            if not computed:
                continue

            name, expression = computed.group(1), computed.group(2)
            if f"nameof({name})" in text:
                continue

            # An expression that mentions nothing but constants cannot go stale.
            if DEPENDS.search(expression):
                suspects.append((str(path.relative_to(root)), name))

    return suspects


def main() -> int:
    root = pathlib.Path(__file__).resolve().parent.parent
    suspects = sweep(root)
    print(f"Unannounced computed properties: {len(suspects)}")
    if suspects:
        print()
        for path, name in suspects:
            print(f"  {path}: {name}")
        print()
        print("  Each is evaluated when the view binds and never again. Where the value can")
        print("  change, the feature is bound and permanently showing what was true at startup.")
        print()
        print("  Most of these are wrapper classes replaced wholesale when their list is rebuilt,")
        print("  which behaves like a record and is safe. The ones to look at are properties on a")
        print("  long-lived page or map view model that reads state which arrives later.")

    print()
    print("Reported, not enforced: a property set once in a constructor never goes stale.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
