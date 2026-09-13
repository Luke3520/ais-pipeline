#!/usr/bin/env bash
# Take a newly published DMA file all the way to the site.
#
# The archive publishes daily with a three-day lag, so a store in real use is built one file at a
# time. Ingest is idempotent by natural key, and detection and the export are full recomputes, so
# running this twice on the same file is a no-op -- proved for the pipeline by
# IncrementalIngestTests, which asserts that files ingested separately land where one ingest would.
#
# Usage:  ./scripts/refresh.sh data/aisdk-2026-09-08.zip [more files...]
set -euo pipefail
cd "$(dirname "$0")/.."

DB="${AIS_DB:-data/ais.db}"
SHIP_TYPE="${AIS_SHIP_TYPE:-Tanker}"

if [ "$#" -eq 0 ]; then
  echo "usage: $0 <file.zip|file.csv> [more files...]" >&2
  exit 2
fi

for source in "$@"; do
  if [ ! -f "$source" ]; then
    echo "no such file: $source" >&2
    exit 2
  fi
done

echo "==> ingest ($# file(s) into $DB, scoped to $SHIP_TYPE)"
for source in "$@"; do
  dotnet run --project src/AisPipeline.Cli -c Release -- \
    ingest "$source" --db "$DB" --ship-type "$SHIP_TYPE"
done

# Stops and port calls are projections over position_report and are rebuilt wholesale, so this is
# what lets a stop left open at the end of yesterday's file be completed by today's (rule 5).
echo "==> detect (full recompute of stops and port calls)"
dotnet run --project src/AisPipeline.Cli -c Release -- detect --db "$DB"

echo "==> export (the file the site builds from)"
dotnet run --project src/AisPipeline.Cli -c Release -- export --out site/src/data --db "$DB"

if [ -d site/node_modules ]; then
  echo "==> site"
  (cd site && npm run build)
else
  echo "==> site skipped: run 'cd site && npm install' first"
fi

echo "==> refreshed. Commit site/src/data/pipeline.json to publish the new figures."
