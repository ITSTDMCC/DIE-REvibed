#!/usr/bin/env bash
# Builds the server and the reference tests with Mono and runs them.
# Needs local/ref from tools/prepare_reference_assemblies.py.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REF="${REF_DIR:-$ROOT/local/ref}"
OUT="${OUT_DIR:-$ROOT/local/test-build}"
mkdir -p "$OUT"
cp "$REF"/*.dll "$OUT"/
mcs -langversion:5 -nowarn:1684 -out:"$OUT/ReferenceTests.exe" \
  -r:"$OUT/Assembly-CSharp.dll" -r:"$OUT/StunCore.dll" -r:"$OUT/ConductorCrafting.dll" -r:System.Core.dll -r:System.Drawing.dll \
  -recurse:"$ROOT/server/src/*.cs" "$ROOT/tests/ReferenceTests.cs" -main:ReferenceTests
mono "$OUT/ReferenceTests.exe"
