#!/usr/bin/env bash
# The full quality gate. Run it before every commit and push; CI runs this same file.
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/dotnet.sh

echo "== build"
"$DOTNET" build Kzg.slnx -c Release

echo "== format"
"$DOTNET" format Kzg.slnx --verify-no-changes

# Every test project runs, each measuring the project it mirrors. Only Kzg.Core's lines have a floor.
for project in tests/*/*.Tests.csproj; do
    covered="$(basename "$project" .Tests.csproj)"
    assembly="$("$DOTNET" msbuild "src/$covered/$covered.csproj" -getProperty:AssemblyName | tr -d '\r')"
    floor=""
    if [ "$covered" = Kzg.Core ]; then
        floor="-p:Threshold=80 -p:ThresholdType=line"
    fi
    echo "== test $covered${floor:+ (coverage floor: 80% of its lines)}"
    # shellcheck disable=SC2086
    "$DOTNET" test "$project" -c Release --no-build \
        -p:CollectCoverage=true -p:Include="[$assembly]*" $floor
done
