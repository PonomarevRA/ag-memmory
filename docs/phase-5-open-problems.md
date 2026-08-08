# Phase 5 — Open problems and follow-up work

> **Owner:** `phase5_initial_schema`  
> **Updated:** 2026-08-08

## P5-01 — Incompatible schema evolution is intentionally not automatic

The LanceDB 2.5.0 .NET binding exposes column-add, alter and drop operations, but Phase 5 does not use them automatically. A type change, required-field change, vector-dimension change, table rename, or coordinated change across the canonical and vector tables cannot be made safe merely by opening a database and applying an SDK operation.

Current behavior is fail-closed: any shape, version or fingerprint disagreement throws `LanceDbSchemaMismatchException` before application data is read or written.

**Safe follow-up strategy:** create new versioned tables (for example `memory_records_v2` and `memory_vectors_v2_<identity-hash>`) or a new versioned storage root; copy data through the canonical model; validate counts, duplicates, scope/lifecycle/expiry behavior and retrieval metrics; create and validate a complete manifest; switch the configured storage root/table set only after validation; keep the previous store read-only until rollback is no longer needed. Do not delete a user store during this process.

## P5-02 — Manifest and data writes are not a cross-table transaction

The manifest is a separate LanceDB table. A process interruption can leave a correctly shaped newly-created vector table without its manifest row, or a manifest row whose table has subsequently been removed outside this adapter. On the former next open, the adapter can register the matching table; on the latter it fails rather than guessing.

**Follow-up:** define recovery/audit tooling that enumerates only adapter-owned tables, compares the manifest with table schemas and produces an operator-approved repair plan. Do not auto-delete orphaned tables.

## P5-03 — LanceDB normalizes the vector child-field nullability on reopen

The adapter creates a non-null `FixedSizeList<float>` vector. LanceDB 2.5.0 returns the list's child field as nullable after reopen even though the list column remains required. The Phase-5 fingerprint therefore validates element type and dimension, and the row reader rejects a null vector component. This preserves runtime correctness but does not make child-element non-nullability an enforceable persisted invariant.

**Follow-up:** verify the binding/native behavior with the target production LanceDB version and, if needed, add an import/audit scan that reports vectors containing null components before indexing.

## P5-04 — Embedding contract version is not a field of `MemoryRecord`

The durable `EmbeddingReference` carries provider, model, `ModelVersion`, dimension, normalization and content hash. It does not carry `EmbeddingContract.Version`; changing Contracts is out of scope for this phase. The manifest exposes `ModelVersion` as the physical embedding-version identity.

**Follow-up:** decide whether a future canonical record needs an explicit embedding-contract version, and specify its migration/re-embedding behavior before introducing a second embedding contract for the same physical identity.

## P5-05 — Numeric and timestamp columns are a compatibility baseline

The Phase-4 mapper uses invariant strings for scalar numbers and ISO-8601 UTC strings for timestamps. This is lossless and validates strictly on reads, but it is not a final analytics-oriented Arrow layout.

**Follow-up:** benchmark a separately versioned typed schema (`Float64`, integer and timestamp fields) against the canonical baseline. Any change must use P5-01's cutover procedure and demonstrate identical scope/status/expiry filtering before it is adopted.
