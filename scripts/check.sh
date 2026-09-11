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

echo "==> unit tests (Core only: no database, no filesystem -- ADR-0003)"
dotnet test tests/AisPipeline.Tests --configuration Release --no-build --nologo -- RunConfiguration.TreatNoTestsAsError=true

echo "==> integration tests (fixture into a temp database; never needs data/)"
dotnet test tests/AisPipeline.IntegrationTests --configuration Release --no-build --nologo -- RunConfiguration.TreatNoTestsAsError=true

echo "==> check passed"
