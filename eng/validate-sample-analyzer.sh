#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repository_root"

if [[ $(uname -s) != Linux || $(uname -m) != x86_64 ]]; then
    echo "Sample analyzer validation currently requires Linux x64." >&2
    exit 1
fi

dotnet_root=$(dirname "$(dotnet --list-sdks | tail -n 1 | sed -n 's/.*\[\(.*\)\]/\1/p')")
reference_directory=$(
    find "$dotnet_root/packs/Microsoft.NETCore.App.Ref" \
        -path '*/ref/net11.0' \
        -type d |
        sort -V |
        tail -n 1
)
output_directory=artifacts/sample-analyzer

if [[ -z "$reference_directory" ]]; then
    echo "The net11.0 reference assembly directory was not found." >&2
    exit 1
fi

dotnet publish \
    src/CscAot/CscAot.csproj \
    -r linux-x64 \
    -c Release \
    --nologo
dotnet publish \
    samples/RoslynAot.SampleAnalyzer.Native/RoslynAot.SampleAnalyzer.Native.csproj \
    -r linux-x64 \
    -c Release \
    --nologo

rm -rf "$output_directory"
mkdir -p "$output_directory"

references=()
for reference in "$reference_directory"/*.dll; do
    references+=("/reference:$reference")
done

compile() {
    local name=$1
    local source=$2
    artifacts/publish/CscAot/release_linux-x64/csc-aot \
        /nologo \
        /nostdlib+ \
        /target:library \
        "/out:$output_directory/$name.dll" \
        "/analyzer:artifacts/publish/RoslynAot.SampleAnalyzer.Native/release_linux-x64/libroslyn-aot-sample-analyzer.so" \
        "${references[@]}" \
        "$source" \
        >"$output_directory/$name.log" 2>&1
}

require() {
    local log=$1
    local pattern=$2
    if ! grep -qF "$pattern" "$output_directory/$log"; then
        echo "FAIL: '$pattern' not found in $output_directory/$log" >&2
        cat "$output_directory/$log" >&2
        exit 1
    fi
}

forbid() {
    local log=$1
    local pattern=$2
    if grep -qF "$pattern" "$output_directory/$log"; then
        echo "FAIL: unexpected '$pattern' in $output_directory/$log" >&2
        cat "$output_directory/$log" >&2
        exit 1
    fi
}

# Bad.cs: AA0001 fires with the expected location; nothing throws.
compile bad samples/Bad.cs
require bad.log "Bad.cs(1,1): warning AA0001: Classes named 'Bad' are not allowed"
forbid bad.log 'warning AD0001:'
[[ -f "$output_directory/bad.dll" ]]

# Throwing.cs: the deliberate analyzer exception surfaces as AD0001 with
# analyzer type, action kind, and exception detail, and the build still
# succeeds and produces output.
compile throwing samples/Throwing.cs
require throwing.log 'warning AD0001:'
require throwing.log 'RoslynAot.SampleAnalyzer.ThrowingAnalyzer'
require throwing.log 'SyntaxNode action'
require throwing.log 'deliberately failed for AD0001 legibility verification'
forbid throwing.log 'warning AA0001:'
[[ -f "$output_directory/throwing.dll" ]]

echo "Sample analyzer validation passed."
