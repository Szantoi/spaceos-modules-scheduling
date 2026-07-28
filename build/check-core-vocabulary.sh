#!/usr/bin/env bash
# ADR-067 / ADR-069 §3 guard: the scheduling CORE must stay industry-neutral.
#
# The woodworking taxonomy belongs to joinerytech.scheduling-standards and the
# Doorstar instance layer. If a domain term leaks into src/, the module stops being
# a horizontal SpaceOS capability -- and that is exactly the drift ADR-067 was
# written to prevent (FlowEpicScope, TenantHandshakeAllowlist).
#
# Scope note: only src/ is scanned. tests/Fixtures holds the Doorstar input pack
# verbatim under a SHA-256 pin, so its customer vocabulary is external provenance
# data, not core code -- rewriting it would break the pin and the contract.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCAN_DIR="$ROOT/src"

# Whole words only. The instance name "Doorstar" is NOT a hit: naming the tenant whose
# reference implementation a rule came from is provenance, and root explicitly requires
# the "doorstar-baseline-v1 (not final)" label in the code. What is banned is the
# industry TAXONOMY leaking into core identifiers and concepts.
#
# Naming rule that keeps this guard simple: for a scheduling time window use "slot" or
# "interval", never "window" -- the latter is a Kernel industry module key.
TERMS='door|cabinet|window|furniture|joinery|timber|lumber|plywood|veneer|sawmill|woodwork|ajtó|ajto|szekrény|szekreny|ablak|bútor|butor|asztalos|faipar|lapszab'

echo "ADR-067 vocabulary guard: scanning $SCAN_DIR"

if matches=$(grep -rEniw "($TERMS)" "$SCAN_DIR" --include='*.cs' 2>/dev/null); then
  echo "FAIL: industry vocabulary found in the scheduling core:" >&2
  echo "$matches" >&2
  echo "" >&2
  echo "Move the term to joinerytech.scheduling-standards (ADR-069 §3) or rename it." >&2
  exit 1
fi

echo "OK: no industry vocabulary in the core."
