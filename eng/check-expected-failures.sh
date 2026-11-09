#!/usr/bin/env bash
# Kontrollerar att resultatfilerna från testgen innehåller exakt de förväntade felen.
# Användning: eng/check-expected-failures.sh <expected-failures.txt> <resultat.json>...
#   t.ex. eng/check-expected-failures.sh samples/expected-failures.txt samples/Shop.Tests/TestResults/testgen/*.json
set -euo pipefail

expected_file="${1:?Ange filen med förväntade fel}"
shift
[ "$#" -gt 0 ] || { echo "Ange minst en resultatfil (TestResults/testgen/<projekt>.json)." >&2; exit 2; }
for result in "$@"; do
  [ -f "$result" ] || { echo "Hittar inte $result. Kördes testerna?" >&2; exit 2; }
done

expected=$(grep -v '^[[:space:]]*\(#\|$\)' "$expected_file" | sort)
actual=$(jq -r '.tests[] | select(.outcome == "failed") | .key' "$@" | sort)

if [ "$expected" == "$actual" ]; then
  echo "Rätt tester föll ($(echo "$actual" | grep -c . || true) st)."
  exit 0
fi
echo "Fel tester föll." >&2
echo "Förväntade:" >&2; echo "$expected" | sed 's/^/  /' >&2
echo "Föll:" >&2; echo "$actual" | sed 's/^/  /' >&2
exit 1
