#!/usr/bin/env bash

# Builds the whole-assembly NativeAOT module plus a handful of single-analyzer
# modules and records size, retained type count, and ILC time against
# eng/module-baseline.json. This is the trimming baseline required by migration
# Step 1.
#
# The whole-assembly module is what the product ships: one native module per
# analyzer assembly. The single-analyzer modules are kept only for sensitivity —
# a rooting regression that moves a 9 MB module by 0.1% moves the floor module
# by a figure a human can read. Which analyzers stand in for that range, and
# why, is in ModuleRunner.s_representatives. Pass --all-modules to sweep every
# analyzer as an audit; that takes tens of minutes and does not touch the
# baseline.
#
# The run publishes each module sequentially; it is deliberately not part of
# validate-differential.sh.
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
