#!/usr/bin/env bash

# Builds one NativeAOT module per analyzer and records size, retained type
# count, and ILC time against eng/module-baseline.json. This is the trimming
# baseline required by migration Step 1.
#
# The run publishes 40+ NativeAOT modules sequentially and takes tens of
# minutes; it is deliberately not part of validate-differential.sh.
#
# Exit codes: 0 match, 2 baseline changed, 3 environment or build error.

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repository_root"

if [[ $(uname -s) != Linux || $(uname -m) != x86_64 ]]; then
    echo "Module measurement currently requires Linux x64." >&2
    exit 3
fi

dotnet build tools/RoslynAot.RoslynFacadeGenerator -c Release --nologo
dotnet build tools/RoslynAot.DifferentialHarness -c Release --nologo

dotnet artifacts/bin/RoslynAot.DifferentialHarness/release/RoslynAot.DifferentialHarness.dll \
    modules --no-publish "$@"
