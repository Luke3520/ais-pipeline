#!/usr/bin/env bash
# Every ADR-NNNN cited in source must resolve to a file in docs/adr/.
#
# Six comments once cited ADR-0014 and ADR-0015 -- numbers reserved during planning and never
# written -- in the same change that wrote the ADR they should have pointed at. A citation that
# resolves to nothing is worse than none in a repo whose own rule is that a structure you cannot
# trace back to a reason is one you cannot safely change.
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
echo "==> all ADR citations resolve"
