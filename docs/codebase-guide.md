# Codebase guide

AgMemory is organised by product capability. Public contracts stay provider-neutral; Core coordinates
authorisation, redaction and transactional workflows; adapters own physical storage details.

## Feature map

- `src/AgMemory.Contracts/Commands` — write commands, outcomes and safe errors.
- `src/AgMemory.Contracts/Queries` — authorised retrieval and context request/result shapes.
- `src/AgMemory.Core/Commands/Handlers` — one handler per durable-memory mutation. The
  `MemoryCommandService` facade owns shared dependencies and exposes the public contract.
- `src/AgMemory.Core/Retrieval`, `HotMemory` and `Decision` — retrieval, short-lived memory and
  decision-specific workflows.
- `src/AgMemory.Storage.LanceDb/Schema`, `Persistence`, `Serialization`, `Query` and
  `Transactions` — feature-owned pieces behind the public `LanceDbMemoryStore` adapter facade.
- `src/Compatibility` — isolated legacy APIs. Do not add new product work there without a
  compatibility requirement and its characterisation tests.

## Non-negotiable boundaries

1. Authorise an exact scope before reading or writing memory.
2. Redact ingress before it can reach a record, receipt or outbox message.
3. Keep idempotency receipt, mutation and content-free outbox staging in one transaction.
4. Keep providers, Arrow/LanceDB and web UI types outside Contracts and Core.
5. Do not treat browser navigation or chat transcripts as durable memory. A memory save is explicit
   and must use the normal command pipeline.

## Working safely

Run the Release test suite before and after behaviour-affecting changes:

```bash
dotnet test ag-memory.slnx -c Release
```

For storage work, start with `tests/AgMemory.Storage.LanceDb.Tests` and
`tests/AgMemory.IntegrationTests`. Preserve schema identity, transaction serialisation and deterministic
result ordering unless a separately reviewed contract change says otherwise.
