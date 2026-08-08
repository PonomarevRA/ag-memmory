# Phase 6 — Open problems

> **Owner:** `phase6_hybrid_retrieval`  
> **Updated:** 2026-08-08

## P6-01 — rendered-token accounting is not yet a budget contract

`TokenBudget` currently constrains the sum of selected `MemoryRecord.EstimatedTokenCost` values.
It does not charge `[memory-id]` prefixes, newline separators, or the serialized citation payload.
This is explicit and tested; changing it requires the versioned Token Economy policy in Phase 11.

## P6-02 — Core does not create query embeddings

The query facade accepts a caller-provided finite vector with a matching reference and policy
contract. It intentionally does not call an embedding provider for raw query text, because query
redaction, provider selection, caching, and latency policy are not yet an approved read-path
contract. Phase 10 must establish that boundary before auto-embedding queries.

## P6-03 — source availability is deliberately coarse

`RetrievalExecution` distinguishes no requested source, completed source, and unavailable source,
without exception messages or storage details. Structured operational diagnostics/metrics should be
added by an observability task; they must not place raw queries or provider exceptions in memory
results.

## P6-04 — graph capability has no production adapter yet

Core accepts an optional `IMemoryGraph` and safely ignores a failed/absent graph. A graph adapter,
its scope-safe relation persistence, and graph quality policy remain future work.

## P6-05 — command extraction is deferred

`MemoryCommandService` remains in `MemoryServices.cs`. The required replay test now characterizes
the existing embedding-before-receipt behavior, but moving command handlers/workflow awaits a
separate extraction task. That task must not silently reverse the embedding/receipt order.

## P6-06 — no production retrieval or SLO claim

The LanceDB integration evidence is local and synthetic. Linux behavior, concurrency, FTS/index
strategy, production corpus quality, latency percentiles, and rollout approval are out of scope
until Phases 9 and 12 provide the necessary fixtures and governance.
