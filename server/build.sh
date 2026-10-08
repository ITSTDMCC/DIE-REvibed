#!/usr/bin/env bash
# Builds EpidemicServer.exe with Mono's C# compiler (for Linux/macOS development).
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p bin
mcs -langversion:5 -out:bin/EpidemicServer.exe -recurse:'src/*.cs'
echo "Built bin/EpidemicServer.exe (run with: mono bin/EpidemicServer.exe)"
