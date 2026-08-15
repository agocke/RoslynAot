#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repository_root"

if [[ $(uname -s) != Linux || $(uname -m) != x86_64 ]]; then
    echo "CA1200 validation currently requires Linux x64." >&2
    exit 1
fi

sdk_line=$(dotnet --list-sdks | tail -n 1)
sdk_version=${sdk_line%% *}
sdk_base=$(printf '%s' "$sdk_line" | sed -n 's/.*\[\(.*\)\]/\1/p')
sdk_directory="$sdk_base/$sdk_version"
dotnet_root=$(dirname "$sdk_base")
reference_directory=$(
    find "$dotnet_root/packs/Microsoft.NETCore.App.Ref" \
        -path '*/ref/net11.0' \
        -type d |
        sort -V |
        tail -n 1
)
analyzer_directory="$sdk_directory/Sdks/Microsoft.NET.Sdk/analyzers"
output_directory=artifacts/ca1200

if [[ -z "$reference_directory" ]]; then
    echo "The net11.0 reference assembly directory was not found." >&2
    exit 1
fi

dotnet publish \
    src/AnalyzeAot.CompilerHost/AnalyzeAot.CompilerHost.csproj \
    -r linux-x64 \
    -c Release \
    --nologo
dotnet publish \
    samples/AnalyzeAot.CA1200Analyzer.Native/AnalyzeAot.CA1200Analyzer.Native.csproj \
    -r linux-x64 \
    -c Release \
    --nologo

rm -rf "$output_directory/managed" "$output_directory/native"
mkdir -p "$output_directory/managed" "$output_directory/native"

common=(
    /nologo
    /nostdlib+
    /target:library
    /deterministic+
    "/pathmap:$repository_root=/_/"
    /analyzerconfig:samples/CA1200.globalconfig
)
references=()
for reference in "$reference_directory"/*.dll; do
    references+=("/reference:$reference")
done

managed=(
    dotnet exec "$sdk_directory/Roslyn/bincore/csc.dll"
    "${common[@]}"
    "/out:$output_directory/managed/CA1200.dll"
    "/doc:$output_directory/managed/CA1200.xml"
    "/analyzer:$analyzer_directory/Microsoft.CodeAnalysis.NetAnalyzers.dll"
    "/analyzer:$analyzer_directory/Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll"
    "${references[@]}"
    samples/CA1200.cs
)
native=(
    artifacts/publish/AnalyzeAot.CompilerHost/release_linux-x64/analyze-aot
    "${common[@]}"
    "/out:$output_directory/native/CA1200.dll"
    "/doc:$output_directory/native/CA1200.xml"
    "/analyzer:artifacts/publish/AnalyzeAot.CA1200Analyzer.Native/release_linux-x64/libanalyze-aot-ca1200-analyzer.so"
    "${references[@]}"
    samples/CA1200.cs
)

"${managed[@]}" >"$output_directory/managed.log" 2>&1
"${native[@]}" >"$output_directory/native.log" 2>&1

cmp "$output_directory/managed.log" "$output_directory/native.log"
cmp \
    "$output_directory/managed/CA1200.dll" \
    "$output_directory/native/CA1200.dll"
cmp \
    "$output_directory/managed/CA1200.xml" \
    "$output_directory/native/CA1200.xml"
grep -F 'warning CA1200:' "$output_directory/native.log"
