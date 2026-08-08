# Phase 5 — Initial LanceDB schema summary

> **Owner:** `phase5_initial_schema`  
> **Status:** complete for the initial, local LanceDB schema policy  
> **Updated:** 2026-08-08

## Outcome

`AgMemory.Storage.LanceDb` now treats its physical tables as a versioned contract rather than accepting any existing LanceDB table with a familiar name.

- `LanceDbMemoryStore.CurrentStorageSchemaVersion` is `1.0`.
- On first open, the adapter creates an internal `agmemory_schema_manifest` table and records each owned table's version, deterministic schema fingerprint, and—when applicable—embedding provider, model, model version, dimension and normalization.
- On every store initialization, the adapter reads the actual Arrow schema and checks ordered field names, nullability, logical types, vector element type and vector dimension before it reads or writes memory data.
- A mismatch raises `LanceDbSchemaMismatchException`; the adapter does not alter, drop, rename, or recreate a mismatched user table.
- Tables created by the Phase-4 adapter are accepted only when their physical shape exactly matches `1.0`; their manifest entries are then added non-destructively.

`GetSchemaManifestAsync()` exposes an adapter-owned, provider-neutral `LanceDbSchemaManifest`. It contains table versions, fingerprints, normalized field descriptions and vector embedding identities, but no LanceDB or Arrow types. This is suitable for a migration manifest or benchmark evidence.

## Initial durable-memory mapping

The baseline uses UTF-8 columns for all canonical scalar values so Arrow values do not leak beyond the adapter. Numeric values and timestamps use invariant, round-trippable strings; structured values use JSON. This is an intentional compatibility baseline, not a claim that it is the final optimized physical representation.

| Canonical concern | `memory_records` columns | Validation / mapping rule |
| --- | --- | --- |
| Identity and exact scope | `id`, `tenant_id`, `project_id`, `workspace_id`, `chat_id`, `run_id` | Tenant is required; every optional scope dimension preserves `null` versus a value. No hierarchy or prefix matching is introduced. |
| Type and lifecycle | `record_type`, `status` | Required, defined `MemoryRecordType` / `MemoryLifecycleStatus` names only. Undefined enum values are rejected before a row is staged. |
| Canonical content | `canonical_text`, `reason` | Canonical text is required; reason remains nullable. |
| Quality and token cost | `importance`, `confidence`, `estimated_token_cost` | Invariant scalar serialization; domain validation enforces finite unit intervals and a positive token cost. |
| Version and timestamps | `created_at_utc`, `updated_at_utc`, `version`, `expires_at_utc` | Required timestamps are UTC; optional expiry remains `null` or UTC. Reads reject a non-UTC persisted offset instead of silently normalizing it. |
| Entities and provenance | `entities_json`, `provenance_json` | Required JSON values are reconstructed and domain-validated. |
| Embedding metadata and durable copy | `embedding_json`, `embedding_vector_json` | The reference preserves provider, model, model version, dimension, normalization and content hash. The durable vector copy remains optional. |
| Vector-search projection | `memory_vectors_<identity-hash>.vector` | Each embedding identity receives a table with `FixedSizeList<float>` of exactly its declared dimension; all canonical fields are copied to support filtered vector search. |
| Provenance for migration / lifecycle | `deduplication_key`, `decision_details_json` | Deduplication key is required; decision detail remains nullable except for `Decision` records, as enforced by the domain. |

The vector table's manifest entry explicitly carries `Provider`, `Model`, `ModelVersion`, `Dimension`, and `Normalization`. `EmbeddingReference` has no separate `EmbeddingContract.Version`; therefore `ModelVersion` is the persisted embedding-version identity at this phase.

## Validation coverage

`AgMemory.Storage.LanceDb.Tests` now proves:

- all initial durable record columns are present with their required/nullability policy;
- schema and embedding metadata survive clean disposal and reopen;
- decision data, expiry and all structured fields round-trip;
- a pre-existing malformed `memory_records` table fails closed and is not replaced;
- separate 3- and 4-dimensional embeddings receive separate, correctly dimensioned vector tables;
- undefined lifecycle enum values cannot be staged; and
- Phase-4 scope isolation, optimistic write and hybrid-port baseline coverage remains green.

Local verification:

```text
dotnet test tests/AgMemory.Storage.LanceDb.Tests/AgMemory.Storage.LanceDb.Tests.csproj --no-restore
passed: 9, failed: 0

dotnet build src/AgMemory.Storage.LanceDb/AgMemory.Storage.LanceDb.csproj --no-restore
succeeded: 0 warnings, 0 errors
```

## Exact input contract for Phase 6

Phase 6 must continue to depend only on `IMemoryStore`, `IVectorSearch`, `ILexicalSearch`, `MemorySearchEligibility`, `SearchPortRequest`, `SearchPortCandidate`, and `MemoryRecord` from `AgMemory.Contracts`.

For every vector request, Phase 6 must supply all of the following:

1. a finite query vector;
2. a query `EmbeddingReference` that matches the supplied `EmbeddingContract`; and
3. an `EmbeddingContract` whose dimension exactly equals the query-vector length.

The adapter uses the embedding identity to select its vector table, validates that table's dimension during open, applies authorization/lifecycle/expiry/type metadata filtering before ranking, and returns `SearchPortCandidate.Embedding` with the persisted embedding reference. Phase 6 must not derive or pass Lance predicates, table names, Arrow schemas or storage paths.

`GetSchemaManifestAsync()` is optional Phase-6 observability input only: a benchmark or diagnostic may attach its result to evidence, but retrieval behavior must not branch on LanceDB-specific fields.

## Safe version policy

There is no automatic schema migration in this phase. A future incompatible schema requires a separate versioned-table or versioned-store cutover, full validation, and an explicit configuration switch. The concrete limitations and safe procedure are tracked in [phase-5-open-problems.md](phase-5-open-problems.md).
