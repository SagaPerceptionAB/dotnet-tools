#!/usr/bin/env bash
# Kontrollerar paketen som dotnet pack gav: rätt antal, inga beroenden på egna paket, om NUnit-paketet finns modulerna inbakade i det, och node för Windows och Linux i testgen-verktyget.
# Användning: eng/check-packages.sh <katalog med .nupkg> [antal paket, standard 4]
set -euo pipefail

feed="${1:?Ange katalogen med paketen}"
expected="${2:-4}"
shopt -s nullglob
packages=("$feed"/*.nupkg)

if [ "${#packages[@]}" -ne "$expected" ]; then
  echo "Förväntade exakt $expected paket, hittade ${#packages[@]}:" >&2
  printf '  %s\n' "${packages[@]##*/}" >&2
  exit 1
fi

failed=0
for package in "${packages[@]}"; do
  if unzip -p "$package" '*.nuspec' | grep -E '<dependency id="SagaPerception\.' >&2; then
    echo "${package##*/} beror på ett eget paket (raden ovan). Modulerna ska vara inbakade, inte egna paket." >&2
    failed=1
  fi
done

nunit=("$feed"/SagaPerception.TestGen.NUnit.*.nupkg)
if [ "${#nunit[@]}" -gt 1 ]; then
  echo "Hittar mer än ett SagaPerception.TestGen.NUnit-paket." >&2
  exit 1
fi
if [ "${#nunit[@]}" -eq 1 ]; then
  contents="$(unzip -Z1 "${nunit[0]}")"
  for dll in SagaPerception.TestGen.NUnit TestGen.Core TestGen.Excel TestGen.Mock TestGen.Browser SagaPerception.CommandLine; do
    if ! grep -qx "lib/net10.0/$dll.dll" <<<"$contents"; then
      echo "${nunit[0]##*/} saknar lib/net10.0/$dll.dll." >&2
      failed=1
    fi
  done
fi

# Verktyget installerar Chromium med Playwrights medföljande node; utan den för användarens plattform går det inte (se TestGen.Cli.csproj).
for cli in "$feed"/SagaPerception.TestGen.Cli.*.nupkg; do
  contents="$(unzip -Z1 "$cli")"
  for node in node/linux-x64/node node/win32_x64/node.exe; do
    if ! grep -q "/\.playwright/$node\$" <<<"$contents"; then
      echo "${cli##*/} saknar Playwrights $node." >&2
      failed=1
    fi
  done
done

[ "$failed" -eq 0 ] && echo "Paketen stämmer: $expected paket, inga egna beroenden, modulerna inbakade i NUnit-paketet om det finns och node för Windows och Linux i testgen-verktyget."
exit "$failed"
