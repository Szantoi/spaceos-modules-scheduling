# spaceos.scheduling

Horizontal, industry-neutral scheduling capability for the SpaceOS platform.
Owner: JoineryTech platform (backend terminal). Contract: **ADR-069**; module
catalogue rules: **ADR-067**; task: **PLAN-03** (EPIC-PRODUCTION-PLANNING-2026Q3).

> **Status: M1 — pure calculation core.** No host, no database, no HTTP surface yet.
> M2 adds persistence + RLS proof, M3 the read-only OpenAPI contract (the Doorstar
> consumption gate).

## What is in here

| Area | Type | Rule |
|---|---|---|
| Effort | `EffortCalculator` | `elapsed = volume × unitMinutes`, `labour = elapsed × workforce`, `days = ceil(elapsed / workingMinutesPerDay) + extraDays` |
| Dependencies | `DependencyBoundResolver` | start branch: fixed override > partial release > FS/SS; finish branch: fixed override > FF/SF — every bound carries its `BoundSource` |
| Networks | `DependencyGraph` | validation (10 issue codes) + deterministic topological order |

Two properties are deliberate and load-bearing:

- **The workforce never shortens elapsed time.** It multiplies labour demand only.
  This is legacy-faithful behaviour, pinned by the compatibility gate.
- **An incomplete standard is flagged, not rejected.** Missing inputs yield a zero
  estimate plus `MissingFields` and `EligibleForAutomaticPlanning = false`, so the
  row stays visible but never gets scheduled automatically.

## The compatibility gate

`tests/.../Fixtures/doorstar-planning-input-pack.v1.json` is a **byte-identical,
SHA-256 pinned** copy of the Doorstar instance's input pack. Its 13 entries
(3 effort vectors + 6 dependency vectors + 3 operation standard samples + 1 calendar
draft) are read from the file at test time rather than transcribed into C#, so the
core cannot drift from the source without failing.

If Doorstar publishes a new pack, the pin fails first. That is intentional: re-verify
the new pack against the published contract, then update the `.sha256` file in the
same commit.

## Open contract question — partial release

Two questions are **unanswered** (JoineryTech backend → Doorstar root, 2026-07-28;
scope boundary confirmed by Doorstar root and the platform root the same day):

1. Does a partial release override the FS bound unconditionally, even when it points
   to a **later** minute?
2. How is the calendar-aware release minute derived from `releaseThresholdPercent`?

Therefore:

- `DependencyBoundResolver.Resolve` takes a **required** `PartialReleasePolicy` — there
  is no default value and no `Default` enum member. Passing `Unspecified` while a
  release is present throws.
- Today's baseline behaviour is labelled `doorstar-baseline-v1 (not final)`
  (`PartialReleaseContract.BaselineLabel`).
- The threshold→minute conversion is an interface (`IPartialReleaseCalculator`) whose
  only implementation (`PendingContractReleaseCalculator`) throws. A guessed formula
  would look plausible and quietly produce wrong schedules.

**The dependency resolver therefore cannot be declared done until both are answered.**

## Industry neutrality

`build/check-core-vocabulary.sh` fails the build if woodworking taxonomy appears in
`src/` (ADR-067 regex guard, ADR-069 §3). The industry vocabulary belongs to
`joinerytech.scheduling-standards`; instance specifics to `doorstar.scheduling-import`.
The test fixture is exempt by design — it is external, hash-pinned provenance data.

Naming rule that keeps the guard simple: a scheduling time window is a **slot** or
**interval**, never a "window" (that word is a Kernel industry module key).

## Build and test

```bash
dotnet build
dotnet test
bash build/check-core-vocabulary.sh
```

Requires the .NET 8 SDK (pinned in `global.json`).
