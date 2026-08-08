# Phase 3 — storage abstraction open problems

> **Owner:** `phase3_storage_abstraction`  
> **Status:** open follow-ups; none is silently resolved by the contract boundary.  
> **Updated:** 2026-08-08

## SA-01 — Core does not yet create a query vector

- **Status:** planned Phase 6 retrieval work.
- **Observation:** `SearchPortRequest` can carry a provider-neutral `ReadOnlyMemory<float>` query vector and an embedding contract, so an adapter has everything needed to execute a vector query. Current `MemoryQueryService` supplies `null`; public query DTOs only hold an `EmbeddingReference`, not vector values.
- **Impact:** a Phase-4 adapter must return no vector candidates when `QueryVector` is absent. It must not derive a vector from text, select a model, or use an embedding provider implicitly.
- **Required action / owner:** retrieval owner defines the query-embedding flow through `IEmbeddingProvider` (or an explicitly versioned client vector input), validates the embedding contract, and adds behaviour tests before hybrid vector recall is enabled.
- **Source:** `src/AgMemory.Contracts/Ports.cs`, `src/AgMemory.Core/MemoryServices.cs`.

## SA-02 — exact selectors are a service-boundary capability, not a cryptographic token

- **Status:** implementation constraint for hosts and the adapter.
- **Observation:** `AuthorizedScopeSet` is an inspectable DTO so adapters and test/host implementations can receive it. The security guarantee is enforced by the command/query service invoking `IAuthorizationScopeValidator` before any port call; the raw store port is not a public client API.
- **Impact:** exposing `IMemoryStore` from an untrusted transport would let a caller construct DTO-shaped input and bypass host authorization.
- **Required action / owner:** composition roots keep raw storage registrations internal and expose only command/query client facades. The LanceDB adapter must still translate every supplied selector into exact metadata predicates and never widen dimensions.
- **Source:** [contracts boundary](contracts.md#граница-public-api), `src/AgMemory.Contracts/Ports.cs`.

## SA-03 — batch API intentionally does not select import semantics

- **Status:** planned Phase 9 migration work.
- **Observation:** `WriteRecordsAsync` provides ordered conditional writes within a transaction but does not define source manifests, import idempotency, partial-batch retry, physical transaction limits, or a retention/delete decision.
- **Impact:** Phase 4 can implement a safe storage primitive, but it cannot claim existing AGM data has been migrated or choose a retry/rollback policy.
- **Required action / owner:** migration owner defines resumable manifests, batch sizing, failure reporting and validation after P0-01/P0-02 approvals.
- **Source:** [migration runbook](migration-from-agm.md), `src/AgMemory.Contracts/Ports.cs`.

## Existing adapter gates

Linux x64 validation, package provenance, FTS/RRF/index/concurrency/schema-evolution checks, and the embedding-policy approval remain owned by the completed LanceDB spike's [open-problems document](phase-3-open-problems.md). This file does not duplicate or close those gates.
