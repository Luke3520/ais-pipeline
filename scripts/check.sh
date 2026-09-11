#!/usr/bin/env bash
# Format, build and test. Runs pre-push and in CI.
# A new blocking check is admitted only for a defect that actually happened --
# see docs/rules/checks-and-review.md.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "==> format"
dotnet format --verify-no-changes

echo "==> build (warnings are errors, see Directory.Build.props)"
dotnet build --configuration Release --nologo

# No TreatNoTestsAsError here yet: Core has no types, so zero unit tests is the correct state.
# This gains the flag with Core's first rule at M1 -- see docs/rules/checks-and-review.md.
echo "==> unit tests (Core only: no database, no filesystem -- ADR-0003)"
dotnet test tests/AisPipeline.Tests --configuration Release --no-build --nologo

echo "==> integration tests (fixture into a temp database; never needs data/)"
dotnet test tests/AisPipeline.IntegrationTests --configuration Release --no-build --nologo -- RunConfiguration.TreatNoTestsAsError=true

echo "==> check passed"
