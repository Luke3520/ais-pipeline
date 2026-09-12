#!/usr/bin/env bash
# ADR integrity, both directions.
#
# 1. Every ADR-NNNN cited in source must resolve to a file in docs/adr/.
#
#    Six comments once cited ADR-0014 and ADR-0015 -- numbers reserved during planning and never
#    written -- in the same change that wrote the ADR they should have pointed at. A citation that
#    resolves to nothing is worse than none in a repo whose own rule is that a structure you cannot
#    trace back to a reason is one you cannot safely change.
#
# 2. Every ADR file must appear in the index in docs/adr/README.md.
#
#    Five ADRs (0032-0036) were written, cited from source, and left out of the index. Check 1
#    passed throughout: it verifies that a cited ADR EXISTS, which says nothing about whether
#    anyone can find it. They were noticed by luck, while looking for something else. An ADR
#    missing from the index is invisible to every reader who opens docs/adr/ rather than grepping
#    source -- which is the reader the index is for.
set -euo pipefail
cd "$(dirname "$0")/.."

missing=0
while read -r ref; do
  n="${ref#ADR-}"
  if ! ls "docs/adr/${n}-"*.md >/dev/null 2>&1; then
    echo "  $ref cited in source but docs/adr/${n}-*.md does not exist:"
    grep -rn "$ref" src tests docs --include="*.cs" --include="*.md" 2>/dev/null \
      | grep -v "^docs/adr/README.md" | sed 's/^/    /' | head -4
    missing=$((missing + 1))
  fi
done < <(grep -rhoE "ADR-[0-9]{4}" src tests docs --include="*.cs" --include="*.md" 2>/dev/null \
         | sort -u)

if [ "$missing" -gt 0 ]; then
  echo "==> $missing dangling ADR citation(s)"
  exit 1
fi

unindexed=0
for record in docs/adr/0*.md; do
  number="$(basename "$record" | cut -c1-4)"
  if ! grep -q "\[$number\](" docs/adr/README.md; then
    echo "  $number is not in the index: $(head -1 "$record" | sed 's/^# //')"
    unindexed=$((unindexed + 1))
  fi
done

if [ "$unindexed" -gt 0 ]; then
  echo "==> $unindexed ADR(s) missing from docs/adr/README.md"
  exit 1
fi

echo "==> all ADR citations resolve, and every ADR is indexed"
