#!/usr/bin/env bash
# Kontrollerar paketen som dotnet pack gav: exakt fyra, inga beroenden på egna paket och modulerna inbakade i NUnit-paketet.
# Användning: eng/check-packages.sh <katalog med .nupkg>
set -euo pipefail

feed="${1:?Ange katalogen med paketen}"
shopt -s nullglob
packages=("$feed"/*.nupkg)

if [ "${#packages[@]}" -ne 4 ]; then
  echo "Förväntade exakt fyra paket, hittade ${#packages[@]}:" >&2
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
[ "${#nunit[@]}" -eq 1 ] || { echo "Hittar inte exakt ett SagaPerception.TestGen.NUnit-paket." >&2; exit 1; }
contents="$(unzip -Z1 "${nunit[0]}")"
for dll in SagaPerception.TestGen.NUnit TestGen.Core TestGen.Excel TestGen.Mock TestGen.Browser SagaPerception.CommandLine; do
  if ! grep -qx "lib/net10.0/$dll.dll" <<<"$contents"; then
    echo "${nunit[0]##*/} saknar lib/net10.0/$dll.dll." >&2
    failed=1
  fi
done

[ "$failed" -eq 0 ] && echo "Paketen stämmer: fyra paket, inga egna beroenden, modulerna inbakade."
exit "$failed"
