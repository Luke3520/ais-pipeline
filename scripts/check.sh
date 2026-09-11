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

# Postgres is included when AIS_POSTGRES is set. compose.yaml provides it:
#   docker compose up -d
#   export AIS_POSTGRES="Host=localhost;Port=55432;Database=ais;Username=ais;Password=ais"
# Without it the suite still runs, against SQLite only -- and AdapterCoverageTests fails in CI
# if that ever happens there.
if [ -n "${AIS_POSTGRES:-}" ]; then
  echo "==> integration tests (SQLite and Postgres)"
else
  echo "==> integration tests (SQLite only; set AIS_POSTGRES to include Postgres)"
fi
dotnet test tests/AisPipeline.IntegrationTests --configuration Release --no-build --nologo -- RunConfiguration.TreatNoTestsAsError=true

echo "==> check passed"
