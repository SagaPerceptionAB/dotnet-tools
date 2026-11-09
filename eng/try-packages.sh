#!/usr/bin/env bash
# Gör det en användare gör med de packade verktygen: ny solution med API, webbapp och xUnit-projekt,
# testgen init från det installerade verktyget, dotnet test och systemmap.
# Användning: eng/try-packages.sh <katalog med .nupkg>
# Arbetskatalog: $WORK_DIR om den är satt, annars en ny temporär katalog.
set -euo pipefail

feed="$(cd "${1:?Ange katalogen med paketen}" && pwd)"
work="${WORK_DIR:-$(mktemp -d)}"
mkdir -p "$work"
work="$(cd "$work" && pwd)"

# Egen NuGet-cache: paketen från den lokala källan ska inte bli kvar i den delade cachen (samma version nästa körning).
export NUGET_PACKAGES="$work/nuget-cache"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

fail() { echo "::error::$*" >&2; exit 1; }
# nuget.config behöver sökvägar som dotnet förstår, även i Git Bash på Windows.
native() { if command -v cygpath >/dev/null; then cygpath -m "$1"; else echo "$1"; fi; }

tools="$work/tools"
demo="$work/Demo"
mkdir -p "$demo"
cd "$demo"

cat > nuget.config <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$(native "$feed")" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

echo "== Ny solution (API Api, webbapp Web, xUnit Unit)"
dotnet new sln -n Demo
dotnet new webapi -n Api
dotnet new webapp -n Web
dotnet new xunit -n Unit
dotnet sln add Api Web Unit

echo "== Installerar verktygen från $feed"
for t in testgen:SagaPerception.TestGen.Cli systemmap:SagaPerception.SystemMap.Cli; do
  dotnet tool install "${t#*:}" --tool-path "$tools" --add-source "$feed" --prerelease
  "$tools/${t%%:*}" --help >/dev/null || fail "${t%%:*} --help misslyckades."
done

echo "== testgen init"
"$tools/testgen" init
test_dir="tests/Demo.Tests"
[ -f "$test_dir/Demo.Tests.csproj" ] || fail "testgen init skapade inte $test_dir/Demo.Tests.csproj."

echo "== Bygger testprojektet och installerar Chromium"
dotnet build "$test_dir"
playwright="$(echo "$test_dir"/bin/Debug/net*/playwright.ps1)"
[ -f "$playwright" ] || fail "Hittar inte playwright.ps1 under $test_dir/bin."
deps=()
[ "$(uname -s)" = "Linux" ] && deps=(--with-deps)
if command -v pwsh >/dev/null; then ps=pwsh; elif command -v powershell >/dev/null; then ps=powershell; else fail "Varken pwsh eller powershell finns, så Chromium kan inte installeras."; fi
"$ps" -NoProfile -File "$playwright" install "${deps[@]}" chromium

echo "== dotnet test på hela lösningen"
dotnet test Demo.slnx --logger "trx" --results-directory "$demo/TestResults" || fail "dotnet test misslyckades. Alla tester ska vara gröna på rena mallar."

echo "== Kontrollerar resultatfilerna"
for project in Api Web; do
  file="$test_dir/TestResults/testgen/$project.json"
  [ -f "$file" ] || fail "testgen skrev ingen resultatfil för $project ($file)."
  n="$(sed -n 's/^ *"tests": *\([0-9][0-9]*\).*/\1/p' "$file" | head -n 1)"
  [ -n "$n" ] && [ "$n" -gt 0 ] || fail "testgen körde inga tester för $project."
  echo "$project: $n tester"
done
grep -rlq 'UnitTest1' "$demo/TestResults" || fail "xUnit-testet (UnitTest1) kördes inte."
echo "xUnit-testet kördes"

echo "== systemmap"
"$tools/systemmap" --solution Demo.slnx --no-browser --json map.json
[ -s map.json ] || fail "systemmap skrev ingen karta (map.json är tom)."
echo "Kartan har $(wc -c < map.json) byte"

echo "Användarens väg fungerar: tester körda, karta skriven."
