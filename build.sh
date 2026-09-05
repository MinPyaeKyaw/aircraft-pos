#!/usr/bin/env bash
# Publishes every Lambda project to src/publish/<ProjectName>.
# Run this before `cdk deploy` or `cdk synth` -- the stack reads from there.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$ROOT/src"
OUT="$SRC/publish"

PROJECTS=(
  PosReportPipeline.IngestApi
  PosReportPipeline.Parser
  PosReportPipeline.Calculator
  PosReportPipeline.StatusApi
)

rm -rf "$OUT"

for project in "${PROJECTS[@]}"; do
  echo "==> Publishing $project"
  dotnet publish "$SRC/$project/$project.csproj" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained false \
    --output "$OUT/$project"
done

echo "==> Published to $OUT"
