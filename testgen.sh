#!/usr/bin/env bash
# Startskript för testgen (macOS/Linux). Exempel: ./testgen.sh web --spec samples/shop.openapi.yaml
# Fungerar från vilken katalog som helst.
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec dotnet run --project "$DIR/testgen/src/TestGen.Cli" -- "$@"
