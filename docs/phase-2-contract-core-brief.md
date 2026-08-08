# Phase 2 — contracts and core implementation brief

> **Analyst card**
>
> - **Owner:** `phase2_analysis`; **consumer:** Phase-2 contracts/core developer.
> - **Status:** ready for implementation of the provider-neutral kernel; this brief does not approve P0 product policies.
> - **Objective:** add `AgMemory.Contracts`, `AgMemory.Core`, and their new test projects without changing the copied `Agm.Memory.*` compatibility projects, AGM, SQLite, LanceDB, or the migration plan status.
> - **Primary sources:** [architecture](architecture.md), [contracts](contracts.md), [migration runbook](migration-from-agm.md), [Phase-0 open problems](phase-0-open-problems.md), compatibility source/tests, and the [synthetic stage-zero corpus](quality-corpus/agm-memory-stage-zero-v1.json).
> - **Definition of done:** Commands are authorised, redacted before any durable boundary, idempotent, and version-safe; retrieval/context code is deterministic and scope-safe; Contracts/Core have no reverse dependency on a storage engine, embedding provider, ASP.NET, Arrow, SQLite, LanceDB, or AGM.
> - **Out of scope:** a durable store or search adapter, actual embedding provider, data import, production policy, Client/MCP facade, and edits below `src/Compatibility/` or `tests/Compatibility/`.

## Human-ready summary

Phase 2 creates the small, testable kernel that every later adapter calls. `AgMemory.Contracts` defines only stable data, outcome and port shapes. `AgMemory.Core` implements the command pipeline, deterministic retrieval fusion, lifecycle rules, deduplication and budgeted context through those ports. The implementation must not open a database, call an embedding SDK, resolve ASP.NET services, or inspect AGM data.

The safe technical default is exact scope matching: a run is not silently widened to a chat, workspace, project, or tenant. A caller supplies an actor and requested scope; Core obtains the only usable selectors from an authorization port. This preserves the future policy choice while preventing the common leak where an adapter performs filtering after candidates have already crossed a tenant boundary.

The existing six-query JSON corpus is useful only as a synthetic regression fixture. It is not evidence of a storage implementation, production relevance baseline, latency baseline, retention policy, or embedding choice.

## Implementation boundary and project graph

Create only the following new paths in the Phase-2 implementation:

```text
src/AgMemory.Contracts/             # BCL + compatible serialization primitives only
src/AgMemory.Core/                  # ProjectReference -> AgMemory.Contracts only
tests/AgMemory.Contracts.Tests/     # references Contracts
tests/AgMemory.Core.Tests/          # references Core and Contracts; in-memory fakes live here
```

`AgMemory.Contracts` must not take a package dependency on a storage engine, provider SDK, DI/host package, or AGM package. `AgMemory.Core` must reference only `AgMemory.Contracts` and the BCL. An in-memory store, fake authorizer, fake redactor, fake embedding provider and capturing outbox are test fixtures, not public runtime adapters.

The developer may adapt/copy deterministic algorithms from the compatibility source, but must not edit or make the new projects depend on `Agm.Memory.*`. In particular, the old `MemoryScope(Guid tenant, Guid? project)`, `MemoryRetrievalScope`, and fan-out scope are reference evidence only; none is a target dependency.

## Required public contract surface

The names below are normative for the Phase-2 public surface. Minor code layout changes are acceptable only if field meaning, outcome semantics and dependency rules are retained. Public payloads and outcomes carry `ContractVersion`; later breaking changes require a new major Contracts package.

### Value objects, scope and canonical data

| Public type | Required shape and validation |
| --- | --- |
| `ScopeId`, `MemoryId`, `CommandId`, `ActorId`, `CorrelationId`, `ContractVersion` | Opaque, non-empty, serializable string value objects. No authorization meaning may be inferred from format. If an input is a GUID, normalize it to canonical lower-case `D` form; otherwise retain only the normalized opaque value. `ScopeId` and `MemoryId` must never be accepted as empty/whitespace. |
| `MemoryScope` | `TenantId`, optional `ProjectId`, `WorkspaceId`, `ChatId`, `RunId`. `TenantId` is required for every record that can be persisted or recalled; every supplied dimension is non-empty. This is a location, not a permission grant. |
| `ScopeSelector` | Wraps one `MemoryScope` and compares **all five** dimensions exactly, including `null`. It has no parent/child or prefix matching behavior. |
| `AuthorizedScopeSet` | Non-empty set of exact selectors returned by authorization. Duplicate selectors are removed deterministically. It is accepted by internal storage/search ports, never trusted from an external command/query caller. |
| `MemoryRecordType` | Existing canonical types `Fact`, `Decision`, `Preference`, `Task`, `Event`, `Procedure`, `Constraint`, `Incident`, `LessonLearned`, plus `Observation`, `Outcome`, `Summary`. |
| `MemoryLifecycleStatus` | `Draft`, `Active`, `Superseded`, `Invalid`. Default recall admits only `Active` and non-expired records. |
| `MemoryRecord` | `Id`, `Scope`, `Type`, `Status`, `CanonicalText`, nullable `Reason`, `Importance`, `Confidence`, `EstimatedTokenCost`, `CreatedAt`, `UpdatedAt`, `Version`, `Entities`, `Provenance`, nullable `Embedding`, nullable `ExpiresAt`, `DeduplicationKey`. Importance/confidence are finite values in `[0,1]`; token cost and version are positive; timestamps are UTC; entities are normalized/deduplicated. `Reason` is nullable but, when present, is normalized and ingress-redacted exactly as canonical text. |
| `MemoryProvenance` and `SourceEvidenceRef` | Preserve source system, legacy record ID, workspace/chat/run/message/execution IDs, and stable evidence/source-fragment identities. They contain references and small approved metadata, never raw source fragments, conversation transcripts, raw logs, or chain-of-thought. `LegacyRecordId` is a traceability key, not an authorization key. |
| `EmbeddingReference` / `EmbeddingContract` | Provider, model, model version, dimension, normalization and content hash. An embedding is optional for a non-vector record, mandatory only when an explicitly configured vector contract requires it. |
| `DecisionDetails` | `Problem`, `Context`, `Options`, `Decision`, `Reason`, `Consequences`, optional `Outcome`; required only for `MemoryRecordType.Decision`. |
| `SessionHotMemory` | Exact `MemoryScope`, content, provenance/evidence references, version, created/updated timestamps and mandatory `ExpiresAt`. It is not a durable canonical summary and must never be returned after expiry. |

`MemoryRecord.Reason` is a required Phase-2 implementation refinement: [the migration mapping](migration-from-agm.md) requires preservation of legacy `workspace_memory.reason`, whereas the proposed `MemoryRecord` sample in [contracts](contracts.md) omits it. Adding this nullable, redacted field preserves parity without changing the required envelope fields. Record the precise name as technical decision `TD-P2-01` in the global problem register; it is not a P0 blocker.

### Commands, queries and typed outcomes

Every mutating command begins with the same envelope:

```csharp
public sealed record CommandEnvelope(
    CommandId CommandId,
    string IdempotencyKey,
    ActorId Actor,
    CorrelationId CorrelationId,
    MemoryScope RequestedScope,
    ContractVersion ContractVersion);
```

`IdempotencyKey` is a bounded opaque value. Its durable uniqueness boundary is `(command kind, authorised location selector, idempotency key)`. It is not globally unique and must not be silently reused across payloads.

Implement these command/query DTOs and result unions (records plus an explicit `Outcome` enum are sufficient; exceptions are not the normal data-path result):

| Surface | Required semantics |
| --- | --- |
| `RememberCommand` / `RememberResult` | Contains an envelope and canonical record input, including type, text, optional reason, evidence references, optional decision details, expiry and desired embedding mode. It returns `Created`, `Reinforced`, `IdempotencyReplay`, `DuplicateConflict`, or a typed failure. A deduplication reinforcement keeps the same memory identity and adds only non-duplicate evidence. |
| `LifecycleCommand` / `LifecycleResult` | Contains envelope, target `MemoryId`, lifecycle action, `ExpectedVersion`, optional related memory, and safe reason. It returns `Applied`, `NotFound`, `StaleVersion`, `InvalidTransition`, or typed failure. Existing records always require an expected version. |
| `AppendHotMemoryCommand` / `HotMemoryResult` | Contains envelope, session content/provenance, absolute expiry (or a caller-supplied TTL translated through `IClock`), capture/idempotency key and `ExpectedVersion`. `0` means create-if-absent only; a pre-existing entry is a conflict. |
| `ForgetMemoryCommand` / `ForgetResult` | A separate explicit delete/forget shape, with `MemoryId` and `ExpectedVersion`; it is never represented as a hidden lifecycle state. Until approved retention/delete policy is supplied it returns `PolicyNotConfigured` without mutation. |
| `MemorySearchRequest` / `MemorySearchResult` | Caller-facing request contains actor, requested scope, query text and/or embedding reference, type/status filters, positive limit and retrieval configuration version. Core constructs its authorised port request after authorization; the caller does not supply `AuthorizedScopeSet`. Results include memory IDs, evidence IDs, provider ranks/scores, RRF contributions, deterministic rank and configuration versions. |
| `MemoryContextRequest` / `MemoryContext` | Caller-facing actor/requested scope, search criteria, explicit positive token budget, citation requirement, and context/retrieval configuration versions. Result contains selected redacted text, citations (`MemoryId` + evidence IDs), estimated token cost, omitted count and configuration versions. It never exceeds the supplied budget. |
| `HotMemoryReadRequest` | Caller-facing actor and exact requested session scope. It returns an eligible `SessionHotMemory` or no item; it must not widen the scope. |

Use one privacy-safe error shape for all typed failures:

```csharp
public sealed record MemoryError(
    MemoryErrorCode Code,
    string? Field,
    string? PolicyOrRuleVersion = null);
```

Allowed `MemoryErrorCode` values for Phase 2 are `InvalidArgument`, `UnsupportedContractVersion`, `Unauthorized`, `RedactionRejected`, `IdempotencyKeyConflict`, `Conflict`, `StaleVersion`, `NotFound`, `InvalidTransition`, `PolicyNotConfigured`, `EmbeddingContractMismatch`, and `DependencyFailure`. Error text, telemetry, receipts and outbox metadata must not echo canonical text, a secret, a query, tenant values, or source content. Throw only for programming-contract violations such as a null service dependency, not for an invalid user command.

### Ports

Place the following public interfaces and their provider-neutral request/result types in `AgMemory.Contracts`. No signature may expose a driver, database connection, Arrow array, SDK embedding object, `IServiceProvider`, ASP.NET request, or `Agm.*` type.

| Port | Minimum responsibility |
| --- | --- |
| `IMemoryCommandService` | `RememberAsync`, `ApplyLifecycleAsync`, `AppendHotMemoryAsync`, and `ForgetAsync`. It owns the normative ingress ordering below. |
| `IMemoryQueryService` | `SearchAsync`, `BuildContextAsync`, and `ReadHotMemoryAsync`. It authorizes before invoking any retrieval port. |
| `IMemoryStore` with `IMemoryStoreTransaction` | Provider-neutral get/list plus a transaction boundary that atomically commits record/evidence/provenance, lifecycle/audit changes, idempotency receipt and outbox record. The conditional write operation must receive `ExpectedVersion` and return the current version on conflict. Batch/import methods may be declared but must not imply a Phase-2 migration implementation. |
| `IVectorSearch` | Accepts an `AuthorizedScopeSet`, active/status/type/expiry filters, query vector/reference, embedding contract and positive limit; returns only scoped candidates with a strictly positive provider rank, provider score and embedding contract reference. |
| `ILexicalSearch` | Same scoping/filter rules as vector search; returns keyword candidates with strictly positive provider rank and provider score. |
| `IMemoryGraph` | Retrieves only authorized entities, relations and graph candidates. It cannot expand a candidate outside `AuthorizedScopeSet`. |
| `IEmbeddingProvider` | Produces an embedding only for an explicitly supplied `EmbeddingContract`; raw vectors use BCL types such as `ReadOnlyMemory<float>`. It has no built-in provider/model fallback. |
| `IAuthorizationScopeValidator` | Given actor, operation and requested scope, returns `Allowed(AuthorizedScopeSet, policyVersion)` or `Denied(safeCode, policyVersion)`. A denial means Core makes no store/search call. |
| `IIngressRedactor` | Returns either accepted redacted/canonicalizable content with rule version and counts, or rejected with only a safe code/rule version. It is invoked before persistence, enqueueing, outbox construction or diagnostic receipt creation. |
| `IClock` and `IIdGenerator` | Respectively return UTC time and new `MemoryId`/`CommandId`; use them for deterministic unit tests. |
| `ICommandReceiver` | Provider-neutral idempotent command-envelope ingress/dispatch boundary for a future host/outbox consumer. It may delegate to `IMemoryCommandService`; it is not an HTTP/MCP interface. |
| `IOutbox` | Participates in the same store transaction via an opaque, content-free `OutboxMessage` and supports durable acknowledgement. It never accepts unredacted payload text. |
| `IRetentionPolicy` and `IEmbeddingPolicy` | Explicit configuration contracts. `IRetentionPolicy` decides only approved delete/purge/hold eligibility; `IEmbeddingPolicy` supplies a versioned embedding contract or an explicit unconfigured state. They prevent Phase 2 from inventing product policy. |

`MemoryStore` is not a public client API. Clients, future MCP tools and migration code use the command/query services so they cannot bypass authorization, redaction, idempotency or version checks.

## Required Core behaviour

### Normative ingress ordering

```text
validate envelope and DTO
  -> authorize requested scope
  -> redact or reject ingress content
  -> canonicalize + validate record shape
  -> calculate deduplication key and token cost
  -> resolve explicitly configured embedding, if requested
  -> one store transaction: conditional record/evidence write + idempotency receipt + outbox record
```

No mutation, outbox message, retry item, diagnostic event or log may be created before successful redaction. An authorization denial and a redaction rejection have no store/outbox side effect. The committed idempotency receipt stores only safe IDs, a canonical payload fingerprint and the durable outcome; an exact replay returns that outcome. Reuse of the same scoped key with a different canonical payload returns `IdempotencyKeyConflict` and does not overwrite the original record.

The Core deduplication key is deterministic over the authorized exact location, type and normalized, redacted canonical text. It must be calculated only after redaction. A duplicate may reinforce an existing record according to the deterministic rule, but must preserve optimistic version semantics for an externally targeted mutation.

### Lifecycle, expiry and concurrency

- Valid lifecycle transitions preserve the compatibility states: `Draft`, `Active`, `Superseded`, `Invalid`. A related record must be inside an authorized exact selector before a relation is made.
- Every mutation of an existing canonical or hot record compares `ExpectedVersion`; a mismatch returns `StaleVersion` with safe current metadata and changes nothing. `ExpectedVersion == 0` is create-if-absent, not overwrite.
- Core filters lifecycle status and expiry before RRF, graph reranking, context building and citation construction. It repeats this guard even if a port claims to have filtered, because ports are extensibility boundaries.
- `SessionHotMemory.ExpiresAt` is absolute; expired data is never returned. Phase 2 does not implement a physical purge job.
- `ForgetAsync` has no default tombstone or purge behaviour. It delegates only to an explicitly configured retention policy and otherwise fails closed with `PolicyNotConfigured`.

### Retrieval and context

Adapt the existing deterministic RRF and context/reranking algorithms into Core, changing their contracts to the unified scope and port types:

1. authorize and obtain exact selectors;
2. read eligible exact hot memory for the requested session selector;
3. invoke lexical and vector ports independently with mandatory scope/status/expiry/type filters;
4. validate positive provider ranks and compatible embedding contracts; fuse with versioned RRF (`k = 60` only as an explicit compatibility configuration);
5. remove duplicate, superseded, invalid and expired records; optionally apply only authorized graph/context reranking;
6. render deterministic cited context within the explicit budget.

Tie breaking is stable by `MemoryId`; result explanations retain each source rank/contribution and retrieval/reranker configuration versions. The former `MinimumMemories = 3` rule must not become a target retrieval requirement. If no regular result is eligible, an explicit configuration may select one eligible, redacted `Summary` fallback; that fallback remains cited and budgeted. Otherwise return an empty context, not invented text or an arbitrary minimum candidate count.

## Configuration that must stay explicit until P0 approval

| Pending decision | Required Phase-2 contract behaviour | Prohibited shortcut |
| --- | --- | --- |
| **P0-01 — scope mapping and authority** | Use `IAuthorizationScopeValidator` and versioned `ScopeAuthorizationResult`. Tests may use fixed synthetic mappings. Production/migration callers cannot obtain selectors without an approved implementation. | Deriving tenant/project from a workspace ID; widening run/chat/workspace/project/tenant in Core or an adapter. |
| **P0-02 — retention, deletion, legal hold** | Declare `IRetentionPolicy` with an `Unconfigured` state. Exclude expired hot memory technically; return `PolicyNotConfigured` for forget/purge/tombstone decisions until a versioned policy is supplied. The known legacy 1 minute–24 hour fan-out TTL range is a compatibility input constraint, not a global default. | Choosing a retention period, physical purge schedule, tombstone scheme, legal-hold cascade, candidate-event eligibility, or extending legacy expiry. |
| **P0-03 — embedding provider/model/version** | Store only an explicit `EmbeddingReference`; require a versioned `EmbeddingContract` before invoking `IEmbeddingProvider`. Unit tests use a fake with declared provider/model/dimension/normalization. | A default provider/model/dimension, implicit re-embedding, mixing incompatible contracts in a search/index, or treating an unembedded record as vector-searchable. |
| **P0-04 — package feed and pinning** | Keep package version/contract-version fields and package projects packable when Phase 1 permits. | Publishing, choosing a registry or claiming independently consumable packages. |
| **P0-05 — `mcp-ai-memory` boundary** | Keep Contracts/Core transport-neutral and expose no MCP or PostgreSQL/pgvector type. | Adding MCP tools, a PostgreSQL adapter, or a convergence path in Phase 2. |

## Acceptance tests and QA cases

All new tests belong under `tests/AgMemory.Contracts.Tests` or `tests/AgMemory.Core.Tests`. The table is the minimum executable acceptance suite; assertions should inspect capturing fakes as well as result values.

| ID | Test case | Required assertion |
| --- | --- | --- |
| C2-01 | Contract value validation | Empty tenant/ID, invalid version, non-positive token cost/version, non-finite importance/confidence, malformed decision details and blank evidence identity produce `InvalidArgument`; a GUID ID round-trips in canonical string form. |
| C2-02 | Dependency/API firewall | Reflection over referenced assemblies and every reachable public member type of Contracts/Core finds no namespace/assembly containing `Lance`, `Arrow`, `Sqlite`, `Microsoft.AspNetCore`, `Agm.`, provider SDKs or `IServiceProvider`. The Core project references only Contracts plus BCL assemblies. |
| C2-03 | Exact scope isolation | Seed same workspace under another tenant, same chat under another run, and a record matching an un-authorized selector. Recall/search/context return none of their record IDs, evidence IDs or citations. Capturing ports receive only validator-issued exact selectors. |
| C2-04 | No implicit widening | A run-scoped request is authorized only for that run; a workspace summary appears only when the validator separately grants the exact workspace selector. A denied request makes zero calls to store/vector/lexical/graph ports. |
| C2-05 | Idempotent write replay | The identical `RememberCommand` returns one memory ID and one durable record/evidence/audit/outbox write; replay returns `IdempotencyReplay` without a second write. |
| C2-06 | Idempotency misuse | Reusing an idempotency key within the same command kind and authorized location with changed normalized payload returns `IdempotencyKeyConflict`; the first record is unchanged. The same key in a different command kind/location is evaluated in its separate allowed boundary. |
| C2-07 | Redaction success and rejection | Capturing store, outbox and log/receipt sink never contain a supplied secret. On a redactor rejection there is no store, outbox, retry or telemetry payload write; response exposes only safe error/rule metadata. |
| C2-08 | Optimistic concurrency | Correct expected version increments exactly once; stale version returns `StaleVersion` and does not mutate; version `0` cannot overwrite an existing canonical or hot-memory entry. Cover lifecycle and hot append separately. |
| C2-09 | Lifecycle/relations | Invalid transitions and cross-selector related records fail closed. Supersede/conflict/undo history remains deterministic and only active, non-expired records enter normal recall. |
| C2-10 | Expiry and policy fail-closed | Expired hot/canonical entries cannot appear in vector, lexical, RRF, graph, context or citations. Forget/delete under unconfigured retention returns `PolicyNotConfigured` and does not mutate. |
| C2-11 | Embedding configuration | A non-vector command invokes no provider. A vector-required command with no configured contract returns `PolicyNotConfigured`. A configured fake creates a matching `EmbeddingReference`; a mismatched result is rejected as `EmbeddingContractMismatch`. |
| C2-12 | Deterministic retrieval | RRF uses best positive rank per source, retains rank contributions/config versions, filters inactive/expired/out-of-scope records before fusion, and breaks equal scores by `MemoryId`. Repeated input has byte-for-byte equivalent ordered output. |
| C2-13 | Context budget and citations | Selected content is redacted, cited with only selected evidence IDs, deterministic and `EstimatedTokenCost <= TokenBudget`. It does not require three candidates. An explicit single-summary fallback is still active, eligible, cited and budgeted. |
| C2-14 | Legacy-shape mapping fixture | A synthetic `Summary` and `SessionHotMemory` preserve workspace/chat/run/message/execution provenance, legacy ID, version, timestamps, expiry and nullable redacted reason. This is a DTO test only; it does not import a database. |
| C2-15 | Stage-zero corpus guard | Parse the six-query JSON corpus as `synthetic-test-fixture` and prove deterministic fixture conversion with supplied synthetic selectors. Do not report Recall/MRR/SLO or claim a real search execution from this test. |

Run the new project tests and the existing compatibility tests independently. The Phase-2 suite must not add an adapter package just to make an integration test convenient.

## Ordered developer work

1. Add the four projects above to `ag-memory.slnx`, preserving existing compatibility project paths and target framework conventions. Give Contracts no non-BCL package references and Core only a Contracts reference.
2. Implement value objects, unified exact scope selectors, enums, canonical records/provenance/embedding/decision/hot-memory DTOs, command/query envelopes, typed results and `MemoryError`. Add C2-01 and C2-14 first.
3. Declare all ports and explicit policy/configuration states. Add C2-02 before any Core work so reverse dependencies fail immediately.
4. Build test-only deterministic fakes: exact-selector in-memory store, authorization validator, redactor, embedding provider, clock/ID generator, outbox and telemetry capture. Keep them out of `src/` public runtime assemblies.
5. Implement the ingress orchestrator in Core in the normative order. Make its transaction contract atomically save the write, idempotency receipt and outbox record. Add C2-03 through C2-11.
6. Port/adapt pure RRF, graph/context reranking and budget rendering from the compatibility core. Replace legacy two-dimensional scope checks with `AuthorizedScopeSet`; remove the arbitrary three-memory minimum. Add C2-12 and C2-13.
7. Run the full new suite plus compatibility suite, inspect package/reference graph, and document any unresolved issue in the central problem register rather than choosing a P0 policy. Phase owner may then mark Phase 2 complete in the migration plan only when the exit criteria below pass.

## Phase-2 exit criteria

- `AgMemory.Contracts` and `AgMemory.Core` compile and their public API firewall test passes without prohibited reverse dependencies.
- At least one Core fixture proves exact scope isolation, no implicit scope widening, idempotent writes and optimistic concurrency.
- Negative redaction tests demonstrate that raw secrets reach neither store, outbox nor diagnostic capture.
- Retrieval/context tests prove deterministic RRF, lifecycle/expiry filtering, citations and strict token budgets.
- Unapproved scope, retention and embedding choices remain explicit configuration/fail-closed states; no production adapter, migration or product default has been introduced.
- Existing `Agm.Memory.*` compatibility projects/tests remain untouched and independently buildable.

## Known risks for the central problem register

| Suggested ID | Risk / evidence | Safe follow-up |
| --- | --- | --- |
| TD-P2-01 | **Required implementation refinement, not a blocker:** `workspace_memory.reason` is required by the Phase-4 migration parity mapping, but the proposed `MemoryRecord` sample omits it except within `DecisionDetails`. Phase 2 adds nullable, redacted `MemoryRecord.Reason`. | Record the exact public-field name in the global register and verify Phase-4 mapping preserves it; do not overload provenance. |
| P2-02 | P0-01 has no approved tenant/project/workspace/chat/run authority mapping. Even technically correct exact matching cannot prove a production selector is the right selector. | Keep real migration/canary blocked; use only fixed synthetic validator fixtures until P0-01 approval. |
| P2-03 | P0-02 leaves purge/tombstone/legal-hold and legacy candidate/event eligibility undefined. | Keep `ForgetAsync` fail-closed and no janitor/migration eligibility default; carry policy version in later manifests. |
| P2-04 | P0-03 leaves vector compatibility unknown. The six-query corpus contains no embeddings or real retrieval execution. | Test only a declared fake embedding contract; defer adapter schema/index and quality claims to Phase 3/5 after P0-03. |
| P2-05 | Current compatibility redaction is distributed. A Core redactor port alone does not prove every future adapter/host avoids plaintext logging. | Make C2-07 mandatory and repeat it for each future adapter/client/MCP integration boundary. |
