# Phase 4 — LanceDB adapter open problems

> **Owner:** `phase4_lancedb_adapter`  
> **Updated:** 2026-08-08  
> **Scope:** unresolved rollout and successor-work items only. The adapter tests are local synthetic evidence, not production approval.

## P4-01 — FTS, hybrid provider fusion and index lifecycle are not accepted

- **Status:** open; Phase 5/6 decision and validation required.
- **Observed:** LanceDB 2.5.0 advertises FTS and index APIs, but this adapter has not proven safe index creation/rebuild, language/tokeniser configuration, reopen behaviour, or provider-side hybrid/RRF semantics. Core owns deterministic RRF already.
- **Current safe behaviour:** `ILexicalSearch` uses deterministic token overlap only after LanceDB applies the exact scope/lifecycle/expiry/type predicate. It meets the current port’s keyword-candidate/rank semantics but is not an FTS relevance or language-quality claim.
- **Required action / owner:** Phase 5 selects schema/index versioning and rebuild procedure; Phase 6 decides whether and how FTS candidates enter Core-owned RRF. Add macOS and Linux tests for create/reopen/rebuild/drop/recreate and a declared language corpus.
- **Source:** `src/AgMemory.Storage.LanceDb/LanceDbMemoryStore.cs`, `docs/phase-3-lancedb-spike.md`.

## P4-02 — physical cross-table atomicity and multi-process concurrency are unverified

- **Status:** open; storage/platform validation required.
- **Observed:** one adapter instance serializes a transaction’s staged read/write/commit lifecycle with `SemaphoreSlim`. LanceDB tables for records, vectors, hot memory, receipts and outbox are distinct, and no binding-backed multi-table transaction or crash-recovery guarantee was established.
- **Impact:** the adapter proves in-process optimistic semantics, not crash-atomic commits or safe simultaneous writers from other processes/instances.
- **Required action / owner:** platform/storage owner defines the supported concurrency topology and failure-recovery/repair protocol; add deterministic multi-instance and injected-failure tests before production writes.
- **Source:** `src/AgMemory.Storage.LanceDb/LanceDbMemoryStore.cs`, `docs/phase-3-open-problems.md`.

## P4-03 — Linux x64 execution remains unverified

- **Status:** open.
- **Observed:** the new adapter suite ran only on macOS arm64, the same host family as the earlier spike.
- **Required action / owner:** run `tests/AgMemory.Storage.LanceDb.Tests` on an approved Linux x64 runner with LanceDB 2.5.0/Arrow 22.1.0 and attach the architecture/package evidence.
- **Source:** local test result; `docs/phase-3-open-problems.md` P3-02.

## P4-04 — current Core cannot supply a query vector

- **Status:** planned Phase 6 retrieval work.
- **Observed:** the vector port accepts a compliant `SearchPortRequest`, but `MemoryQueryService` still passes `QueryVector = null`. The adapter correctly returns no vector candidates rather than embedding query text implicitly.
- **Required action / owner:** Phase 6 defines the query-embedding flow and tests compatible/non-compatible contracts. It must not select an embedding provider or model as an adapter side effect.
- **Source:** `src/AgMemory.Core/MemoryServices.cs`, `docs/phase-3-storage-abstraction-open-problems.md` SA-01.

## P4-05 — initial schema evolution remains a separate Phase-5 decision

- **Status:** planned Phase 5.
- **Observed:** this implementation maps all required canonical fields and uses per-contract fixed vector tables, but it intentionally does not declare a schema version, native scalar-type policy, manifest migration, vector index rebuild or re-embedding transition.
- **Required action / owner:** Phase 5 adopts a versioned schema and a forward/backward migration procedure before storage paths are released.
- **Source:** `docs/phase-4-lancedb-adapter-summary.md`.

## P4-06 — package provenance approval is external

- **Status:** blocked by release/security approval.
- **Observed:** runtime functionality does not approve the non-official-package publisher, signing, SBOM, licence, vulnerability review, update or rollback process.
- **Required action / owner:** security/release owner closes P3-01 before production deployment.
- **Source:** `docs/phase-3-open-problems.md` P3-01.
