# Phase 3 — open LanceDB problems and follow-up gates

> **Owner:** Phase-3 storage/adapter owner  
> **Updated:** 2026-08-08  
> **Status rule:** the macOS spike is evidence for a narrow local path. None of these entries may be treated as silently resolved by its success.

## P3-01 — package provenance must be approved before production adoption

- **Status:** open, release/security decision required.
- **Observation:** the tested package is `LanceDB` 2.5.0, declared as [`lennylxx/lancedb-csharp`](https://github.com/lennylxx/lancedb-csharp) at commit `44bc124616d479b8afce2568d1592d95c27d2e92`. Its package description says it wraps the official Rust `lancedb` crate, but it is not published from the `lancedb` GitHub organisation.
- **Impact:** a working macOS spike is insufficient supply-chain approval for a durable production data store.
- **Required action / owner:** security/release owner validates publisher identity, package signing/SBOM/vulnerability posture, licence and upgrade/rollback process. Record the approved version/source before `AgMemory.Storage.LanceDb` is released.
- **Evidence:** [phase-3 spike report](phase-3-lancedb-spike.md), package `lancedb.nuspec` after restore.

## P3-02 — Linux x64 execution is still a mandatory runtime gate

- **Status:** open, platform-validation task required.
- **Observation:** `LanceDB` 2.5.0 includes `runtimes/linux-x64/native/liblancedb_ffi.so`, but this workspace is macOS arm64. The Linux binary was inspected as ELF x86-64; it was not loaded or executed.
- **Impact:** Phase 3 cannot claim macOS + Linux support and an adapter must not be promoted based on the macOS-only run.
- **Required action / owner:** run the exact spike project on a clean supported Linux x64 machine with the pinned SDK/package; attach `dotnet --info`, architecture and command output. Add it to CI only after the runner/version policy is approved.
- **Evidence:** [phase-3 spike report](phase-3-lancedb-spike.md).

## P3-03 — adapter operational guarantees need dedicated integration tests

- **Status:** open, adapter implementation gate.
- **Observation:** the spike proves create/open/reopen, Arrow mapping, batch add/upsert, metadata filtering, vector query and delete. It deliberately does not exercise concurrent connections, FTS/hybrid query/RRF, indexing/rebuild, schema evolution or failure recovery beyond clean close/reopen.
- **Impact:** these missing checks are explicit Phase-3 exit criteria, so no production adapter is ready yet.
- **Required action / owner:** adapter developer adds isolated integration tests for each operation on macOS and Linux; Core retains deterministic RRF so provider-specific fusion cannot alter public ranking semantics implicitly.
- **Evidence:** [migration plan Phase 3](memory-migration-plan.md#3-lancedb-technical-spike-and-adapter), [phase-3 spike report](phase-3-lancedb-spike.md).

## P3-04 — embedding contract is not selected

- **Status:** blocked by P0-03; out of scope for the spike.
- **Observation:** test vectors are fixed-size three-dimensional synthetic values solely to verify Arrow/LanceDB mapping. They are not a recommendation for model, dimension, normalisation or re-embedding policy.
- **Impact:** the production table/index partition and vector query validation cannot be finalised safely.
- **Required action / owner:** ML/platform owner approves P0-03, then adapter developer versions table schema and rejects or partitions incompatible embedding contracts.
- **Evidence:** [phase-0 open problems](phase-0-open-problems.md), [contracts embedding rules](contracts.md#canonical-data).

## P3-05 — predicate construction must stay inside the adapter

- **Status:** open, implementation constraint.
- **Observation:** the binding's filtering API accepts SQL-like predicate text. The spike intentionally uses literals to prove engine filtering; it does not establish a safe public query language.
- **Impact:** forwarding arbitrary client filter strings could bypass validation or make scope filtering inconsistent.
- **Required action / owner:** storage adapter exposes no raw predicate API. It derives exact `tenant/project/workspace/chat/run`, status, type and expiry predicates from already-authorised, validated port inputs; tests prove no cross-scope candidate can reach ranking.
- **Evidence:** [scope invariants](contracts.md#scope-invariants), [phase-3 spike report](phase-3-lancedb-spike.md).

## Handoff

The next adapter owner starts with the runnable spike and its pinned package versions, but must not copy its raw SQL predicate construction into public APIs. The minimum next deliverable is an internal `AgMemory.Storage.LanceDb` schema mapper plus macOS/Linux integration tests that close P3-02, P3-03 and P3-05. P3-01 and P3-04 require their named decision owners; they cannot be self-approved by adapter code.
