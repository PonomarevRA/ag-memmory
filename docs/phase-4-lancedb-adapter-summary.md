# Phase 4 — LanceDB adapter summary

> **Owner:** `phase4_lancedb_adapter`  
> **Status:** complete for the local macOS arm64 implementation and synthetic integration suite; rollout gates remain open.  
> **Updated:** 2026-08-08

## Outcome

`AgMemory.Storage.LanceDb` is now the only production project that references `LanceDB` 2.5.0 and `Apache.Arrow` 22.1.0. Its public surface exposes only `AgMemory.Contracts` and BCL types:

- `LanceDbMemoryStore` implements `IMemoryStore`, `IVectorSearch` and `ILexicalSearch`;
- `LanceDbMemoryStoreOptions` accepts the local database path;
- LanceDB `Connection`/`Table`, Arrow schemas/record batches and textual predicates are private implementation details.

The adapter provides durable canonical records, session hot memory, idempotency receipts and outbox messages. `IMemoryStoreTransaction` stages writes under a per-instance async gate, preserves the order and result correspondence of `WriteRecordsAsync`, applies exact-scope optimistic checks before mutation, and persists only on `CommitAsync`.

Every store/search path derives predicates internally from `AuthorizedScopeSet`, `ScopeSelector`, or `MemorySearchEligibility`; it never receives a caller-provided predicate. Exact tenant/project/workspace/chat/run matching, `Active` status, expiry, and optional type filtering happen in the LanceDB query before lexical or vector ranks are assigned. A synthetic scope containing a quote is covered by an isolation test.

## Vector and lexical paths

Vector tables are created lazily per explicit embedding contract identity (provider/model/version/dimension/normalization) and use an Arrow `FixedSizeList<float>` whose dimension comes from that contract. This avoids inventing a global vector dimension while P0-03 is unresolved. Query vectors must be present, finite, and compatible with the supplied contract; otherwise the port fails rather than selecting a provider/model implicitly.

The adapter currently uses a deterministic token-overlap lexical fallback over pre-filtered LanceDB rows. It returns positive ranks and finite scores through `ILexicalSearch`; it does not expose FTS syntax or select tokenisation/language policy. FTS/index validation is intentionally retained as an open rollout gate.

## Necessary provider-neutral contract repair

The adapter exposed two defects that prevented correct durable vector storage:

1. `MemoryRecord` only retained an `EmbeddingReference`, while `IEmbeddingProvider` produced the vector and `MemoryCommandService` discarded it. `MemoryRecord.EmbeddingVector` now stores an optional BCL `ReadOnlyMemory<float>` and validates reference, dimension, and finite values. Core preserves the generated vector after a compatible embedding result.
2. `MemoryRecord.Validate()` treated a missing `ExpiresAt` as an invalid non-UTC value. It now accepts `null` and still rejects a non-UTC supplied expiry.

Neither repair introduces LanceDB/Arrow types into Contracts or Core.

## Local validation

On macOS arm64 with .NET SDK 10.0.102 and the pinned packages:

```text
dotnet test tests/AgMemory.Storage.LanceDb.Tests/AgMemory.Storage.LanceDb.Tests.csproj --no-restore
  passed: 4, failed: 0

dotnet test tests/AgMemory.Contracts.Tests/AgMemory.Contracts.Tests.csproj --no-restore
  passed: 7, failed: 0

dotnet test tests/AgMemory.Core.Tests/AgMemory.Core.Tests.csproj --no-restore
  passed: 12, failed: 0

dotnet build ag-memory.slnx --no-restore
  succeeded: 0 warnings, 0 errors
```

The adapter suite proves, using only synthetic data:

- ordered conditional batch upsert, persistence, clean disposal/reopen, and list/get;
- durable hot memory, receipt and outbox writes;
- lexical and cosine vector retrieval with pre-ranking exact scope/status/expiry/type exclusion;
- no foreign update/delete and stale-version rejection; and
- opaque scope literal escaping without widened results.

## Exact handoff to Phase 5 — schema policy

Phase 5 owns versioning and schema evolution. The implemented mapper gives it these concrete inputs:

| Physical table family | Durable fields currently mapped | Phase-5 decision still needed |
| --- | --- | --- |
| `memory_records` | ID; five exact scope dimensions; type/status; canonical/reason text; importance/confidence/token cost; UTC timestamps/version; entities; provenance; embedding reference/vector; expiry; deduplication key; decision details | schema version, native Arrow types vs current lossless text/JSON mapping, migration/rebuild protocol, retention/tombstones |
| `memory_vectors_<hash>` | complete canonical record mapping plus non-null `FixedSizeList<float>` vector | stable naming/partition/version key, index choice/rebuild policy, embedding contract transition/re-embedding |
| `session_hot_memory` | exact scope key, ID/content/provenance/version/UTC lifecycle timestamps | hot-memory expiry/decay/promotion policy belongs to Phase 7 |
| `idempotency_receipts`, `outbox_messages` | exact scope, idempotency/receipt or safe outbox metadata | atomicity/outbox delivery, audit retention and recovery protocol |

The actual source of truth for the current mapper is [`LanceDbMemoryStore.cs`](../src/AgMemory.Storage.LanceDb/LanceDbMemoryStore.cs); Phase 5 must treat it as an implementation baseline, not an approved final schema policy.

## Not claimed

This result does not claim Linux execution, multi-process concurrency, cross-table crash atomicity, FTS/RRF/index rebuild, schema migration, production embedding quality, legacy import, or AGM rollout. Those items are isolated in [the Phase-4 problem file](phase-4-open-problems.md).
