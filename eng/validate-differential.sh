#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repository_root"

if [[ $(uname -s) != Linux || $(uname -m) != x86_64 ]]; then
    echo "Differential harness validation currently requires Linux x64." >&2
    exit 1
fi

dotnet build tools/RoslynAot.DifferentialHarness -c Release --nologo

harness_dll=artifacts/bin/RoslynAot.DifferentialHarness/release/RoslynAot.DifferentialHarness.dll

exec dotnet "$harness_dll" run "$@"
