#!/usr/bin/env bash
# Reports what the schema and the source write and never read.
#
# Three of these turned up in one night -- a map definition cache that was filled and never
# consulted, a raid position table whose rows were written on every screenshot and read by
# nothing, and a bounds description that named a building nowhere. Each cost an hour of
# reading code that looked correct because it was correct; the data simply never arrived.
#
# So the sweep runs on every build, and reports rather than fails. A new table is legitimately
# unread on the commit that creates it, and a gate here would only teach people to route
# around it. What it buys is that nobody has to remember to look.
#
# Two questions, each a grep:
#   1. which tables in the migrations are named by no query in the source
#   2. which of those the source writes to but never reads from
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
migrations="$root/src/TarkovCompanion.Infrastructure/Persistence/Migrations"
sources=("$root/src")

# Tables the migrations create and have not since dropped. A later migration dropping one is
# the fix for exactly what this sweep reports, so counting it would keep reporting the thing
# that was already dealt with.
mapfile -t dropped < <(
    grep -ho 'DROP TABLE \(IF EXISTS \)\?[A-Za-z_][A-Za-z0-9_]*' "$migrations"/*.sql |
        sed 's/DROP TABLE \(IF EXISTS \)\?//' |
        sort -u
)
mapfile -t tables < <(
    grep -ho 'CREATE TABLE \(IF NOT EXISTS \)\?[A-Za-z_][A-Za-z0-9_]*' "$migrations"/*.sql |
        sed 's/CREATE TABLE \(IF NOT EXISTS \)\?//' |
        sort -u |
        grep -vxF -f <(printf '%s\n' ${dropped[@]+"${dropped[@]}"}) || true
)

# Everything the source could say about a table, excluding the migrations themselves: a
# migration naming a table is not the application reading it.
scan() {
    grep -rn --include='*.cs' --include='*.sql' -w "$1" "${sources[@]}" |
        grep -v '/Persistence/Migrations/' || true
}

unread=()
never=()
for table in "${tables[@]}"; do
    hits="$(scan "$table")"
    if [[ -z "$hits" ]]; then
        never+=("$table")
        continue
    fi

    # A read is a SELECT, a JOIN or a FROM naming it. A write is INSERT, UPDATE or DELETE.
    reads="$(printf '%s\n' "$hits" | grep -ci 'FROM[[:space:]]*$\|FROM[[:space:]]\+'"$table"'\|JOIN[[:space:]]\+'"$table" || true)"
    writes="$(printf '%s\n' "$hits" | grep -ci 'INSERT[[:space:]]\+INTO[[:space:]]\+'"$table"'\|UPDATE[[:space:]]\+'"$table"'\|DELETE[[:space:]]\+FROM[[:space:]]\+'"$table" || true)"
    if [[ "$reads" -eq 0 && "$writes" -gt 0 ]]; then
        unread+=("$table ($writes write(s), 0 reads)")
    fi
done

echo "Schema sweep: ${#tables[@]} tables in the migrations."
echo
if [[ ${#never[@]} -eq 0 ]]; then
    echo "No table is unmentioned by the source."
else
    echo "Tables the source never names (${#never[@]}):"
    printf '  %s\n' "${never[@]}"
fi

echo
if [[ ${#unread[@]} -eq 0 ]]; then
    echo "No table is written and never read."
else
    echo "Tables written and never read (${#unread[@]}):"
    printf '  %s\n' "${unread[@]}"
    echo
    echo "  Each of these is a feature that runs, records what it found, and cannot show it."
fi

echo
echo "Reported, not enforced: a table is legitimately unread on the commit that creates it."
