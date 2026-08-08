# Phase 3 — storage abstraction summary

> **Owner:** `phase3_storage_abstraction`  
> **Status:** complete (contract boundary only)  
> **Updated:** 2026-08-08

## Result

`AgMemory.Contracts` now contains a provider-neutral storage boundary suitable for a future LanceDB adapter without allowing LanceDB, Apache Arrow, SQLite, ASP.NET, or AGM types into Contracts/Core. This task does **not** create, configure, or register an adapter.

The boundary makes two rules executable at every retrieval call:

1. the storage/retrieval call receives validator-issued exact selectors; and
2. normal recall ranks only `Active`, non-expired records as of the supplied UTC instant.

Core repeats the eligibility check during deterministic RRF, so a faulty or incomplete provider filter cannot promote an out-of-scope, inactive, or expired record into context.

## Port inventory

| Port | Exact responsibility and required input |
| --- | --- |
| `IMemoryStore` | Starts a transaction and reads a record/list/hot-memory only with `AuthorizedScopeSet`. |
| `IMemoryStoreTransaction` | Looks up idempotency/deduplication state, writes/deletes canonical records, writes hot memory, persists safe receipts, and enqueues outbox messages. Every data operation takes `AuthorizedScopeSet`; receipt and outbox operations additionally carry an exact `ScopeSelector`. `WriteRecordsAsync` supports ordered conditional batches inside the caller's transaction. |
| `IVectorSearch` | Receives `SearchPortRequest`: `MemorySearchEligibility`, text, optional BCL `ReadOnlyMemory<float>` query vector, optional embedding reference/contract, and limit. It returns provider rank/score only after applying the eligibility predicate. |
| `ILexicalSearch` | Uses the same `SearchPortRequest` and eligibility boundary for keyword candidates. |
| `IMemoryGraph` | Reranks only the candidate list supplied by Core and receives the same `MemorySearchEligibility`; it must not expand beyond the authorised/active/non-expired set. |

`MemorySearchEligibility` is an explicit, provider-neutral value object. It contains `AuthorizedScopeSet`, optional type filtering and `AsOfUtc`; its fixed normal-recall lifecycle status is `Active`, and `ExpiresAt <= AsOfUtc` is excluded. It is a retrieval rule, not a retention, delete, tenant-mapping, or embedding-provider policy.

`OutboxMessage` is now exact-scope-bound. This lets an adapter persist/audit a content-free outbox row without inferring its location from an ID.

## Dependency firewall and contract proof

[`StoragePortContractTests`](../tests/AgMemory.Contracts.Tests/StoragePortContractTests.cs) verifies:

- every storage data operation requires `AuthorizedScopeSet`;
- outbox messages are exact-scope-bound;
- vector, lexical, and graph ports receive explicit pre-ranking eligibility;
- active/non-expired/type/exact-scope eligibility rejects foreign-run, invalid, expired, and wrong-type records; and
- reflection over every exported Contracts/Core public signature and its referenced assemblies finds no `Lance`, `Arrow`, `Sqlite`, `Microsoft.AspNetCore`, `Agm.`, or `IServiceProvider` dependency/type.

## Validation

```text
dotnet build ag-memory.slnx --no-restore
  succeeded: 0 warnings, 0 errors

dotnet test tests/AgMemory.Contracts.Tests/AgMemory.Contracts.Tests.csproj --no-build
  passed: 6, failed: 0, skipped: 0

dotnet test tests/AgMemory.Core.Tests/AgMemory.Core.Tests.csproj --no-build
  passed: 12, failed: 0, skipped: 0
```

## Phase 4 adapter handoff

Implement `AgMemory.Storage.LanceDb` as the **only** project that references `LanceDB` and `Apache.Arrow`. Keep connection/table/schema/record-batch/predicate objects internal. Map all five exact scope dimensions, type, lifecycle status and expiry into physical metadata, then derive predicates only from `MemorySearchEligibility` and `ScopeSelector`; do not accept a caller-supplied predicate string.

The adapter must return positive provider ranks and finite scores, apply the eligibility predicate before assigning those ranks, and use `QueryVector` only when it is present and compatible with the supplied embedding contract. Implement both single and ordered batch conditional writes through the transaction port; do not invent migration, retention, or embedding defaults.

Open follow-ups are isolated in [phase-3-storage-abstraction-open-problems.md](phase-3-storage-abstraction-open-problems.md).
