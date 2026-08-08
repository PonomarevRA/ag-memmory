# Phase 6 — Hybrid Retrieval summary

> **Owner:** `phase6_hybrid_retrieval`  
> **Status:** complete for the local provider-neutral retrieval slice  
> **Updated:** 2026-08-08

## Result

`AgMemory.Core` now executes the retrieval path as independently testable components under
`src/AgMemory.Core/Retrieval/`:

```text
authorized query
  -> exact eligibility + compatible optional vector
  -> lexical and vector ports started independently
  -> deterministic RRF + cross-ID deduplication
  -> optional graph rerank
  -> exact hot memory, optional summary fallback, cited context packing
```

Core remains dependent only on `AgMemory.Contracts` and BCL types. It has no LanceDB, Arrow,
SQLite, filesystem, transport, or AGM dependency. The LanceDB adapter continues to receive only
the provider-neutral `SearchPortRequest` boundary.

## Delivered behavior

- `MemorySearchRequest` and `MemoryContextRequest` can carry an optional BCL
  `ReadOnlyMemory<float>` query vector. Core forwards it only together with a finite, dimension-
  matching `EmbeddingReference` and policy `EmbeddingContract`.
- When no compatible vector exists, Core still invokes both ports with an empty semantic input;
  the vector adapter returns no vector candidates and lexical retrieval remains usable.
- `HybridSearchExecutor` starts lexical and vector searches before awaiting either. A failed or
  absent optional semantic/graph capability cannot discard eligible lexical results.
- `RetrievalExecution` exposes only provider-neutral source state (`NotRequested`, `Completed`,
  `Unavailable`); it never exposes exception text, storage details, or predicate details.
- Eligibility is sent to every port and repeated in Core before RRF: exact validator-issued scope,
  `Active` lifecycle, non-expiry at one `AsOfUtc`, and optional record type.
- Deterministic RRF preserves lexical/vector ranks and contributions, applies stable `MemoryId`
  ties, and collapses cross-ID candidates with the same deduplication key. Superseded records are
  ineligible before ranking.
- `ContextBuilder` prioritizes non-expired exact-scope hot memory, removes repeated IDs,
  deduplication keys, and canonical text, emits sorted evidence citations, and never selects a
  total `EstimatedTokenCost` above `TokenBudget`.
- A fallback reads at most one authorized, active, non-expired `Summary` only when no candidate
  remains and `AllowSingleSummaryFallback` is explicit.

## File-level structure

| File | Responsibility |
| --- | --- |
| `Retrieval/MemoryQueryService.cs` | Public `IMemoryQueryService` facade and authorization boundary. |
| `Retrieval/RetrievalRequestFactory.cs` | Exact eligibility and compatible semantic-input construction. |
| `Retrieval/HybridSearchExecutor.cs` | Parallel ports, RRF, optional graph fallback, execution metadata. |
| `Retrieval/DeterministicRetrieval.cs` | Provider-rank validation, eligibility recheck, stable RRF/deduplication. |
| `Retrieval/HotMemoryReader.cs` | Exact, authorized, non-expired hot read. |
| `Retrieval/SummaryFallbackReader.cs` | Explicit single eligible-summary fallback. |
| `Retrieval/ContextBuilder.cs` | Deterministic context selection, citations, and estimate-budget enforcement. |

The command facade intentionally remains in `MemoryServices.cs`; moving it is a separate,
characterized extraction rather than a Phase-6 concern.

## Verification

The focused suite proves:

- required-embedding replay keeps the current pre-receipt embedding call timing;
- public query vectors are provider-neutral contract values and are forwarded through search and
  context paths;
- scope/run isolation, invalid/superseded lifecycle, expiry, and type filtering occur before
  ranks;
- lexical results survive absent or failing vector/graph capabilities;
- same-key durable duplicates collapse deterministically;
- hot memory precedes durable results, citations are deterministic, and selected estimated cost
  can equal but never exceed the requested budget;
- real LanceDB vector + lexical search receives the Core request and cannot surface foreign,
  inactive, or expired records.

See the final task evidence for the exact full-solution test command and result.

## Handoff

Phase 7 can use `MemoryQueryService.ReadHotMemoryAsync` and `ContextBuilder`'s current exact TTL
visibility as its baseline, but must add bounded state, decay, and promotion through a new policy
slice rather than mutating retrieval rules.

Phase 10 should call `IMemoryQueryService.SearchAsync` or `BuildContextAsync`; a client/MCP host
must supply the actor and requested exact scope, and may provide a query vector only with a
matching embedding reference. It must return `RetrievalExecution` unchanged instead of exposing
provider failures or provider types.
