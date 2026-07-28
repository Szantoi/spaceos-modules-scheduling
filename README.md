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
| Kernel scope | `KernelWorkScope` | opaque Project → FlowEpic → Task link; authorisation and revision validation stay in the published kernel handshake |

Two properties are deliberate and load-bearing:

- **The workforce never shortens elapsed time.** It multiplies labour demand only.
  This is legacy-faithful behaviour, pinned by the compatibility gate.
- **An incomplete standard is flagged, not rejected.** Missing inputs yield a zero
  estimate plus `MissingFields` and `EligibleForAutomaticPlanning = false`, so the
  row stays visible but never gets scheduled automatically.

## The compatibility gate

Two Doorstar input packs are pinned side by side, each a **byte-identical, SHA-256
pinned** copy: `v1` (13 entries) and `v2` (14 entries, adding the settled
partial-release vector and superseding v1). Every entry is read from the file at test
time rather than transcribed into C#, so the core cannot drift from the source without
failing.

Both are kept because a version number must identify exactly one content: v1 is what an
older consumer may still hold, v2 is current. If Doorstar publishes a new pack, the pin
fails first — re-verify it against the announcement, then update the `.sha256` file in
the same commit.

## Partial release — the settled rule

Both questions were answered by the business owner on 2026-07-28 and carried into
ADR-069 §4:

1. A partial release **overrides the FS bound unconditionally**, even when it points to a
   later minute. Because that can delay work the dependency would have allowed earlier,
   the resolver attaches `DependencyWarning.PartialReleaseDelaysStart` — equality is not a
   delay and does not warn.
2. The release minute is **proportional to WORKING time** on the predecessor's calendar
   (breaks and closures excluded), rounded **up** to the next working minute
   (`WorkingTimeReleaseCalculator`). Rounding up because releasing early means releasing
   against unfinished output.

The label of the rule set in force is `doorstar-contract-v1 (final)`
(`PartialReleaseContract.ContractLabel`). The earlier `PartialReleasePolicy` parameter and
the throwing calculator are gone: they existed only to stop an undecided rule from being
assumed silently.

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
