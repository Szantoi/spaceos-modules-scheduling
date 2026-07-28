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
INCLUDES=(--include='*.cs' --include='*.sql' --include='*.json' --include='*.csproj' --include='*.yaml' --include='*.yml')

# Two lists, because one matching mode cannot serve both languages.
#
# WORD_TERMS are matched as WHOLE WORDS: as substrings they would fire on innocent
# code -- "door" inside the instance name Doorstar (which root explicitly requires in
# the "doorstar-baseline-v1" label), "tok" inside "token", "mdf" inside a hash.
WORD_TERMS='door|cabinet|window|furniture|joinery|timber|lumber|plywood|veneer|sawmill|woodwork|mdf|tok|lamella|fólia|folia|zsanér|zsaner|vasalat'
#
# SUBSTRING_TERMS are matched ANYWHERE, because Hungarian compounds glue the domain
# word to its neighbour ("Ajtólap", "tokmag", "élzárásPerc") and whole-word matching
# would sail straight past them. Only accented/unambiguous stems are listed: a bare
# "pres" would hit "present"/"expression", so it is not here.
SUBSTRING_TERMS='ajtó|ajto|szekrény|szekreny|ablak|bútor|butor|asztalos|faipar|lapszab|furnér|furner|élzár|elzar|prés|forgács|forgacs|rétegelt|retegelt|pácol|pacol|csiszol'

echo "ADR-067 vocabulary guard: scanning $SCAN_DIR"

hits=''
if word_hits=$(grep -rEniw "($WORD_TERMS)" "$SCAN_DIR" "${INCLUDES[@]}" 2>/dev/null); then
  hits+="$word_hits"$'\n'
fi
if substring_hits=$(grep -rEni "($SUBSTRING_TERMS)" "$SCAN_DIR" "${INCLUDES[@]}" 2>/dev/null); then
  hits+="$substring_hits"$'\n'
fi

if [ -n "${hits//[$'\n']/}" ]; then
  echo "FAIL: industry vocabulary found in the scheduling core:" >&2
  printf '%s' "$hits" | sort -u >&2
  echo "" >&2
  echo "Move the term to joinerytech.scheduling-standards (ADR-069 §3) or rename it." >&2
  exit 1
fi

echo "OK: no industry vocabulary in the core."
