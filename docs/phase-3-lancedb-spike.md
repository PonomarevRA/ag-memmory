# Phase 3 — LanceDB .NET technical spike

> **Owner:** `phase3_lancedb_spike`  
> **Status:** complete for the macOS arm64 operations below; this is not an adapter implementation or a Linux validation.  
> **Checked:** 2026-08-08 on macOS 27 arm64 with .NET SDK 10.0.102.

## Summary

The current `LanceDB` .NET binding can persist and reopen a local LanceDB table on this macOS arm64 host. The executable spike successfully maps memory-like metadata and fixed-size float vectors through Apache Arrow, writes a batch, applies metadata predicates, performs scoped cosine vector search, merge-insert upsert, predicate delete, and closes/reopens the table without loss of the expected state.

This result proves a useful **local macOS feasibility slice**, not the Phase-3 adapter exit criteria. In particular, it does not prove Linux runtime behaviour, concurrent access, full-text or hybrid RRF retrieval, index rebuild, schema evolution, or production embedding policy.

## Binding and runtime

| Item | Verified value |
| --- | --- |
| NuGet package | [`LanceDB` 2.5.0](https://www.nuget.org/packages/LanceDB/2.5.0) |
| Direct Arrow dependency | `Apache.Arrow` 22.1.0 |
| Binding repository/commit declared by package | [`lennylxx/lancedb-csharp`](https://github.com/lennylxx/lancedb-csharp), `44bc124616d479b8afce2568d1592d95c27d2e92` |
| Native implementation declared by package | P/Invoke wrapper over the official Rust `lancedb` crate |
| Host actually executed | macOS 27, `arm64`, .NET SDK 10.0.102 |
| Native library actually loaded | `liblancedb_ffi.dylib`, Mach-O `arm64` |

The package contains native artifacts for macOS arm64, Linux x64 and Windows x64. Only the macOS arm64 artifact was executed here. The package is published by `lennylxx`, not the `lancedb` GitHub organisation; adoption therefore needs a supply-chain/provenance decision before a production adapter is approved.

## What the executable proves

Source: [`spikes/AgMemory.LanceDb.Spike/Program.cs`](../spikes/AgMemory.LanceDb.Spike/Program.cs).

| Operation | Result | Check performed |
| --- | --- | --- |
| Connect and create a local table | Passed | `Connection.Connect` + `CreateEmptyTable` with an explicit Arrow schema. |
| Map metadata and vectors | Passed | Strings for `id`, tenant/workspace, lifecycle status, type, text and embedding model; `FixedSizeList<float>[3]` for `vector`. |
| Batch add | Passed | One Arrow `RecordBatch` containing four records persisted and counted. |
| Metadata filtering | Passed | `tenant_id = 'tenant-a' AND status = 'Active'` returns exactly two allowed rows. |
| Scoped vector search | Passed | Cosine nearest-neighbour query plus the same predicate returns `memory-1` first and no foreign-tenant row. |
| Batch upsert | Passed | `MergeInsert("id")` updates `memory-2` and inserts `memory-5`; count becomes five. |
| Delete | Passed | `Delete("id = 'memory-4'")` removes the superseded record. |
| Reopen/restart recovery | Passed | Dispose first connection/table, create a fresh connection, `OpenTable`, then recover four expected rows and scoped filter results. |

The spike creates a GUID-named temporary database directory and deletes only that directory in `finally`; it does not modify AGM or a production data path.

## Reproduction

From the repository root:

```bash
dotnet restore spikes/AgMemory.LanceDb.Spike/AgMemory.LanceDb.Spike.csproj --configfile NuGet.Config
dotnet build spikes/AgMemory.LanceDb.Spike/AgMemory.LanceDb.Spike.csproj -c Release --no-restore
dotnet run --project spikes/AgMemory.LanceDb.Spike/AgMemory.LanceDb.Spike.csproj -c Release --no-build
```

Observed final command output:

```text
PASS: create table and add Arrow-mapped metadata/vector batch.
PASS: metadata filter is applied before result materialisation.
PASS: scoped cosine vector search.
PASS: batch merge-insert upsert.
PASS: metadata predicate delete.
PASS: reopen local table and read durable data.
PASS: LanceDB local durability spike completed.
```

## Deliberately unverified

| Requirement | State | Reason / next owner |
| --- | --- | --- |
| Linux runtime behaviour | Unverified | This is a macOS arm64 host. Run the same executable on a supported Linux x64 runner and attach its output. |
| Concurrent access | Unverified | Needs a deterministic multi-connection test and documented expected write/read semantics. |
| Full-text search and RRF/hybrid fusion | Unverified | Spike scope proves vector path and raw filters only; Phase-3 adapter needs tests for provider ranking and Core-owned deterministic RRF. |
| Index creation/rebuild | Unverified | Add an integration test large enough for the selected index and verify a reopen/rebuild scenario. |
| Schema evolution | Unverified | Add explicit migration-version tests before fixing table schema policy. |
| Embedding contract compatibility | Blocked by P0-03 | The synthetic three-dimensional vectors prove Arrow shape only; they do not select provider/model/dimension/normalization or re-embedding behaviour. |
| Raw-filter safety | Open design constraint | The binding accepts SQL-like predicate strings. The future adapter must construct server-side predicates from authorised, validated selectors and never forward caller-supplied filter text. |

## Adapter developer handoff

The Phase-3 adapter may reference only `LanceDB` and `Apache.Arrow`, with both references confined to `AgMemory.Storage.LanceDb`. Keep `Connection`, `Table`, Arrow `Schema` and `RecordBatch` internal to that project. Translate the future Contracts/Core ports into the tested physical fields, including exact scope fields, lifecycle status, expiry and embedding-contract partitioning. Do not expose Arrow/LanceDB types in Contracts, Core, Client, MCP or migration public APIs.

Before an adapter is considered ready, add integration coverage for the deliberately unverified items above, in particular a Linux x64 run, cross-tenant negative queries, lifecycle/expiry filtering before ranking, index rebuild and concurrent access. The open decisions and supply-chain gate are tracked in [phase-3-open-problems.md](phase-3-open-problems.md).
