# Архитектура реализации Memory Platform — фазы 6–12

> **Architect card**
>
> - **Владелец:** `implementation_architecture`.
> - **Статус:** blueprint для отдельных implementation-задач; production-код этим документом не меняется.
> - **Граница:** планирует фазы 6–12 и безопасное разбиение `AgMemory.Core/MemoryServices.cs`; не принимает product/security policy и не меняет публичные контракты без отдельной contract-задачи.
> - **Источник текущей семантики:** `src/AgMemory.Core/MemoryServices.cs`, `DeterministicRetrieval.cs`, public Contracts и core tests. Если документ и исполняемый код расходятся, до отдельного одобренного semantic delta источником истины остаётся код с characterization-тестами.

## Итоговое решение

`MemoryServices.cs` не следует переписывать одной большой заменой. Его надо разложить по
операциям и read-path, сохранив namespace `AgMemory.Core`, публичные имена
`MemoryCommandService` и `MemoryQueryService`, их конструкторы и результаты. Так физическое
перемещение файлов не станет breaking change для потребителя.

Один command handler владеет ровно одной мутацией. Общая внутренняя workflow-boundary владеет
проверкой envelope, exact-scope authorization, redaction, безопасным fingerprint и границей
транзакции. Handlers не обращаются к `IMemoryStoreTransaction` и не формируют outbox напрямую
в обход неё. Это сохраняет единственный проверяемый путь записи, но устраняет класс на 605 строк.

```text
public command facade
        |
        +--> operation handler (remember / lifecycle / hot / forget)
                    |
                    +--> command workflow (auth, redaction, receipt, transaction)
                    |
                    +--> IMemoryStoreTransaction (record + receipt + outbox + commit)

public query facade
        |
        +--> hybrid search executor --> lexical + vector --> RRF --> graph
        |
        +--> context builder <---------- exact hot memory / eligible summary fallback
```

## Неподвижные зависимости

| Папка / проект | Разрешены | Запрещены |
| --- | --- | --- |
| `src/AgMemory.Contracts` | BCL | LanceDB, Arrow, SQLite, ASP.NET, AGM, DI host types |
| `src/AgMemory.Core/{Commands,Retrieval,HotMemory,Decision,TokenPolicy,Migration}` | `AgMemory.Contracts`, BCL | любой provider, transport, database и AGM |
| `src/AgMemory.Storage.LanceDb` | `AgMemory.Contracts`, LanceDB/Arrow только внутри adapter-а | public LanceDB/Arrow API, AGM |
| `src/AgMemory.Client` | Contracts, BCL | LanceDB, SQLite, filesystem path, host DI |
| `src/AgMemory.McpServer` | Client, Contracts, MCP transport | прямой store/adapter access и legacy SQLite |
| `src/AgMemory.Migration` | Contracts, Core; SQLite package только в source-reader части | `ProjectReference` на AGM, runtime dual-write |
| `tools/AgMemory.MemoryMigrationBenchmark` | Client, Contracts; fixture readers | AGM source reference и production-data assumptions |

`AgMemory.Storage.LanceDb` продолжает реализовывать порты, но не получает ссылку на Core.
Core и Contracts проходят существующий reverse-dependency test. Composition root находится только
в host/CLI/MCP project.

## Целевое дерево файлов и владение

Ниже указаны целевые файлы. Пометка **new contract** означает отдельную сначала выполненную
задачу владельца Contracts; implementation-задача не добавляет такие публичные типы сама.

```text
src/
├── AgMemory.Core/
│   ├── Commands/
│   │   ├── MemoryCommandService.cs             # public facade; сохраняет IMemoryCommandService/ICommandReceiver
│   │   ├── CommandWorkflow.cs                  # internal единственная точка безопасной write-sequence
│   │   ├── CommandPreflight.cs                 # internal envelope, authorization, exact requested selector, redaction
│   │   ├── RememberCommandHandler.cs            # internal remember/reinforce/dedup/embedding
│   │   ├── LifecycleCommandHandler.cs           # internal validated state transitions
│   │   ├── HotMemoryCommandHandler.cs           # internal current append semantics
│   │   ├── ForgetMemoryCommandHandler.cs        # internal retention-gated delete
│   │   └── CommandValueSupport.cs               # internal canonicalize, entity normalization, hash, token estimate
│   ├── Retrieval/
│   │   ├── MemoryQueryService.cs                # public IMemoryQueryService facade
│   │   ├── HybridSearchExecutor.cs              # internal authorization-to-port request, parallel search, RRF, graph
│   │   ├── RetrievalRequestFactory.cs           # internal eligibility and compatible vector request construction
│   │   ├── ContextBuilder.cs                    # internal bounded, cited deterministic packing
│   │   ├── HotMemoryReader.cs                   # internal exact-scope, non-expired hot read
│   │   ├── SummaryFallbackReader.cs             # internal explicit single-summary fallback
│   │   ├── DeterministicRetrieval.cs            # current public static RRF type, only moved physically
│   │   └── MemoryCoreOptions.cs                 # existing public versioned technical defaults
│   ├── HotMemory/
│   │   ├── SessionHotMemoryCoordinator.cs       # Phase 7, after policy/contract prerequisites
│   │   ├── HotMemoryDecayEvaluator.cs           # Phase 7, internal pure decay/expiry calculation
│   │   └── HotMemoryPromotionService.cs         # Phase 7, promotes only through command workflow
│   ├── Decision/
│   │   ├── DecisionMemoryService.cs             # Phase 8, record compact DecisionDetails through remember path
│   │   └── DecisionTraceValidator.cs            # Phase 8, rejects incomplete/non-compact trace input
│   ├── TokenPolicy/
│   │   ├── TokenCostEstimator.cs                # extracts current estimate before any later policy change
│   │   ├── MemoryIngressPolicy.cs               # Phase 11, reusable-content and duplicate gate
│   │   └── ContextBudgetPlanner.cs              # Phase 11, budget allocation/reporting around ContextBuilder
│   └── Migration/
│       └── MemoryImportService.cs               # Phase 9, only after import/ledger contract exists
├── AgMemory.Client/                             # new project, transport-neutral facade
│   ├── AgMemory.Client.csproj
│   ├── MemoryClient.cs                           # Recall/Search/Remember facade over Contracts services
│   ├── MemoryClientOptions.cs                    # client-side supported contract/version validation only
│   └── DecisionClientExtensions.cs               # only when a stable decision command is approved
├── AgMemory.McpServer/                           # new host project
│   ├── AgMemory.McpServer.csproj
│   ├── Program.cs                                # composition root only
│   ├── Authorization/McpActorResolver.cs         # maps authenticated transport identity; no selector widening
│   ├── Tools/MemoryRecallTool.cs
│   ├── Tools/MemorySearchTool.cs
│   ├── Tools/MemoryRememberTool.cs
│   ├── Tools/MemoryRecordDecisionTool.cs
│   └── Transport/McpRequestMapper.cs             # transport DTO/error mapping, never business policy
├── AgMemory.Migration/                           # new executable; optional legacy path
│   ├── AgMemory.Migration.csproj
│   ├── Program.cs
│   ├── Source/VersionedExportReader.cs
│   ├── Source/SqliteSnapshotReader.cs            # tooling-only SQLite dependency
│   ├── Mapping/LegacyMemoryConverter.cs
│   ├── Execution/MigrationRunner.cs
│   ├── Execution/ResumableBatchImporter.cs
│   ├── Validation/MigrationValidator.cs
│   └── Reporting/MigrationManifestWriter.cs
└── (future only after Contracts approval)
    └── AgMemory.Contracts/
        ├── HotMemory.cs                          # typed hot-state/policy command values, if required
        ├── DecisionCommands.cs                   # RecordDecisionCommand/result, if facade cannot use RememberCommand
        └── Migration.cs                          # import command, ledger and manifest ports

tools/
└── AgMemory.MemoryMigrationBenchmark/
    ├── AgMemory.MemoryMigrationBenchmark.csproj
    ├── Program.cs
    ├── Evaluation/BenchmarkRunner.cs
    ├── Evaluation/RetrievalMetricCalculator.cs
    ├── Evaluation/LatencyPercentiles.cs
    ├── Reporting/BenchmarkReportWriter.cs
    └── Fixtures/                                 # synthetic fixture only; never production export

tests/
├── AgMemory.Core.Tests/
│   ├── Commands/{Remember,Lifecycle,HotMemory,Forget}CommandHandlerTests.cs
│   ├── Commands/CommandWorkflowInvariantTests.cs
│   ├── Retrieval/{HybridSearch,ContextBuilder,HotMemoryReader,SummaryFallback}Tests.cs
│   ├── HotMemory/{Decay,Promotion}Tests.cs       # Phase 7
│   ├── Decision/DecisionMemoryServiceTests.cs    # Phase 8
│   └── TokenPolicy/{Ingress,Budget}Tests.cs      # Phase 11
├── AgMemory.Client.Tests/MemoryClientParityTests.cs
├── AgMemory.McpServer.Tests/MemoryToolParityTests.cs
├── AgMemory.Migration.Tests/{Mapping,Resume,Validation}Tests.cs
├── AgMemory.Benchmark.Tests/{Metrics,ReportSchema}Tests.cs
├── AgMemory.IntegrationTests/
│   ├── CommandWorkflowStoreInvariantTests.cs
│   └── ClientMcpStoreParityTests.cs
└── AgMemory.Storage.LanceDb.Tests/               # adapter-specific schema/index/reopen tests remain here
```

Existing test files can be moved in the same extraction-only change, but their test names and
fixtures must remain until all newly placed tests prove identical outcomes. New test projects are
added to `ag-memory.slnx` together with their production project; a folder without a project is
not a completed phase.

## `MemoryServices.cs` extraction map

| Existing responsibility | Target file | Must remain true after physical split |
| --- | --- | --- |
| `MemoryCommandService` public methods and `ICommandReceiver.ReceiveAsync` | `Commands/MemoryCommandService.cs` | Same public signatures, contract-version failures and result shapes. |
| `RememberAsync` | `Commands/RememberCommandHandler.cs` | Redacted canonical content only; reinforcement merges deduplicated evidence deterministically; optional embedding stays explicit. |
| `ApplyLifecycleAsync` and transition table | `Commands/LifecycleCommandHandler.cs` | Expected version, related-record visibility and all transition outcomes stay unchanged. |
| `AppendHotMemoryAsync` | `Commands/HotMemoryCommandHandler.cs` | `ExpectedVersion == 0` remains create-only; current append/merge behaviour is preserved until Phase 7 replaces it deliberately. |
| `ForgetAsync` | `Commands/ForgetMemoryCommandHandler.cs` | Missing retention policy remains fail-closed; deletion stays optimistic and transactional. |
| `AuthorizeAsync`, envelope validation, requested selector lookup | `Commands/CommandPreflight.cs` | Validator-issued exact selectors are never widened or synthesized by Core. |
| receipt lookup, conflict/replay logic, receipt/outbox/commit | `Commands/CommandWorkflow.cs` | A successful mutation has record/hot/deletion, receipt and outbox staged in one transaction and committed once. |
| `Canonicalize`, entities, scope key, hash, token estimate | `Commands/CommandValueSupport.cs`, later `TokenPolicy/TokenCostEstimator.cs` | Algorithms are copied verbatim in extraction PR; a later policy PR owns any behaviour change. |
| `MemoryQueryService.SearchAsync` | `Retrieval/HybridSearchExecutor.cs` behind the facade | Port calls stay after authorization, lexical/vector remain concurrent, RRF and stable tie-break stay deterministic. |
| `BuildContextAsync` | `Retrieval/ContextBuilder.cs` plus hot/fallback readers | Exact hot scope, budget, citation toggle, output order and one-summary fallback semantics stay unchanged. |
| `ReadHotMemoryAsync` | `Retrieval/HotMemoryReader.cs` | Expired or unauthorized memory is invisible and no scope is widened. |

The first developer change may leave `MemoryServices.cs` as a temporary empty compatibility file
only if the project style requires it; it must not retain duplicate implementations. The preferred
end state removes it after `MemoryCommandService` and `MemoryQueryService` are compiled from their
target files.

## Command workflow: mandatory invariants

Every command handler uses the same logical pipeline. `Forget` has no content to redact/embed;
its corresponding stages are explicitly not applicable, rather than bypassed accidentally.

```text
validate envelope/input
  -> authorize actor and obtain validator-issued exact selector
  -> redact or reject every persisted content field
  -> canonicalize and validate redacted values
  -> fingerprint/idempotency replay-or-conflict check
  -> calculate token cost and create embedding only when explicitly required
  -> stage domain mutation + receipt + content-free outbox in one transaction
  -> commit once
```

The non-negotiable write properties are:

1. Authorization happens before redaction, embedding, store access or retrieval. A denied command
   does not start a transaction.
2. Nothing unredacted reaches a record, hot memory, receipt, outbox or diagnostic payload.
   Redaction failure leaves store and outbox untouched.
3. The same `(command kind, exact selector, idempotency key)` either replays the same fingerprint
   or returns `IdempotencyKeyConflict`; it cannot create a second record/evidence/outbox row.
4. `Remember` invokes `IEmbeddingProvider` only for `EmbeddingMode.Required` and only after
   redaction. The returned reference must match the policy contract and the vector remains
   provider-neutral BCL data.
5. `WriteRecordAsync`/`WriteHotMemoryAsync`/`DeleteRecordAsync`, `SaveReceiptAsync`,
   `EnqueueAsync` and `CommitAsync` belong to one `IMemoryStoreTransaction`. No handler reports
   success before the commit succeeds.
6. Outbox content remains identifiers, versions and safe metadata only. A new helper must not turn
   the outbox into a second persistence channel for raw command content.

### Required critic decision before order changes

The desired logical order says **idempotency before embedding**, while the current
`RememberAsync` creates an embedding before opening a transaction and looking up its receipt.
The store interface exposes receipt lookup only through `IMemoryStoreTransaction`, so literal
idempotency-before-transaction is not currently expressible. This is a real design tension, not a
file-move detail.

Therefore the extraction task must preserve the observed order (`authorize → redact → canonicalize
→ embed when required → transaction/receipt`) and add a replay characterization test for embedding
calls. A separate approved semantic delta may instead open a transaction, inspect the receipt,
then embed and stage/commit. It must define transaction duration, retry behaviour and concurrent
same-key semantics. The critic owns the recommendation; a developer must not silently "fix" it
inside a mechanical split.

## Retrieval and context ownership — Phase 6

`HybridSearchExecutor` receives a validated request only after `MemoryQueryService` authorizes it.
It builds one `MemorySearchEligibility` with the authorization result and a single `AsOfUtc`, calls
lexical and vector ports in parallel, fuses only provider-issued positive ranks, performs
deduplication and then asks the graph to rerank the already eligible candidates. It must retain:

- scope/status/expiry/type filtering before every provider rank and a second Core eligibility check;
- current RRF constant/configuration version and stable `MemoryId` tie-break;
- only finite graph scores for records already in the fused candidate set;
- no Lance predicate, table name, Arrow data or storage path;
- the existing `QueryVector`/embedding-contract validation handoff from Contracts rather than
  deriving an embedding model in Core.

`ContextBuilder` owns only selection and rendering. It prepends an eligible exact-scope hot item,
then search hits; scans in that deterministic order; skips items that do not fit; emits citations
from sorted evidence identities; and never exceeds `TokenBudget`. `SummaryFallbackReader` runs only
when the result is empty and `AllowSingleSummaryFallback` is explicit. It returns one eligible,
authorized `Summary`, never a fabricated candidate set.

Phase-6 acceptance tests belong in `Retrieval/` and cover: authorization denial with zero port
calls; foreign/inactive/expired rejection; lexical/vector parallel call results; RRF contribution
explanation; duplicate key collapse; graph non-expansion; hot-before-durable ordering; exact budget;
citations; and fallback eligibility. Adapter filtering/reopen/index tests remain in the LanceDB test
project.

## Phase gates after retrieval

| Phase | Independently owned implementation slice | Prerequisites and implementation boundary | Completion evidence |
| --- | --- | --- | --- |
| 7. Hot memory | `HotMemory/*` plus approved typed Contract values if needed | Current `SessionHotMemory` is an unstructured string and has no bounded-size, decay or promotion policy. Do not infer categories/TTL/retention; obtain approved policy port/value first. Promotion calls command workflow, never writes store directly. | Unit tests for bound, expiry, decay and promotion idempotency; integration test proves expired/foreign data never enters context. |
| 8. Decision memory | `Decision/*`; only `DecisionCommands.cs` if a specialized public command is approved | Existing `DecisionDetails` already represents Problem/Context/Options/Decision/Reason/Consequences/Outcome and current redactor handles it. Reuse remember ingress and persist only compact reusable details; no chain-of-thought field or transcript. | Required-field/redaction/idempotency tests; decision retrieval/citation test; API test proves no raw reasoning field exists. |
| 9. Existing migration | `AgMemory.Migration/*` + approved `Contracts/Migration.cs`/`Core/Migration/MemoryImportService.cs` | Optional path only. It reads an approved versioned export or immutable snapshot, not AGM runtime or project source. Ledger, deterministic target identity and batch transaction must be public provider-neutral capabilities before implementation. | Synthetic `--dry-run`, resume, changed-source conflict, `--validate-only`, parity/duplicate/missing-data manifest tests. No real import or deletion claim. |
| 10. Agent integration | `AgMemory.Client/*`, `AgMemory.McpServer/*` | Client is a thin facade over command/query services. MCP authenticates to an actor and invokes the identical client methods; transport cannot grant selectors or convert denial to empty success. `memory_record_decision` waits for a stable decision command/facade. | Shared conformance fixture runs Recall/Search/Remember/Decision through client and MCP; scope, error, idempotency, citations and budget are identical. |
| 11. Token economy | `TokenPolicy/*` and ContextBuilder integration | First extract the existing `ceil(chars / 4)` estimate untouched. Any new ingress/no-conversation/no-log/duplicate/ref-preference policy must arrive via an approved port/configuration version, not hidden constants. | Rejected raw-log/conversation fixtures leave no state; all durable records have cost/importance/confidence; duplicate and budget reports are deterministic. |
| 12. Validation | `tools/AgMemory.MemoryMigrationBenchmark/*` and its tests | Benchmarks consume query results and manifests, not provider internals. Fixture source is declared synthetic until an approved corpus exists; no SLO is invented. | Versioned JSON/CSV report gives Recall@5, Recall@20, MRR, p50/p95, context tokens and duplicate rate; metric/percentile tests pass. |

## Migration and benchmark flow

The optional migration executable follows this exact ownership boundary:

```text
approved export/snapshot
  -> source reader -> converter -> Core import service -> provider-neutral transaction/ledger
  -> LanceDB adapter
  -> manifest + validation report
  -> benchmark/shadow reader (read-only comparison)
```

`SqliteSnapshotReader` is isolated to the migration executable. It must not be referenced by
Client, MCP or normal service hosts. The converter does not authorize a scope itself; it passes
the approved import identity/scope through Core, which invokes the same redaction and validation
boundary as normal ingestion. Dry-run creates no storage, receipt, outbox or ledger mutation.

The benchmark has two inputs with equal query fixtures: the prior-result fixture/export and the
new Client-based retrieval result. It records opaque IDs/evidence IDs, query-set/configuration
versions, per-query latency and token estimates. It must label the corpus `synthetic` unless the
data owner approved a real export; it does not log raw content or assert production parity from a
synthetic set.

## Test and review sequence for a developer

1. Add characterization tests around all public outcomes in the existing Core test files, notably
   denied/redaction/replay/receipt/outbox/optimistic-version behaviour and current vector call
   timing. These tests are written before moving a method.
2. Extract command support and one handler at a time. After each handler, run Core plus Contracts
   tests. Do not combine with hot-memory policy, decision API, client or schema edits.
3. Extract retrieval orchestration and context packing separately. Run Core, Contracts and LanceDB
   adapter tests because `SearchPortRequest` semantics cross that boundary.
4. The critic reviews public API diff, dependency firewall, transaction/replay tests and the order
   decision above. Only then can a semantic change be proposed in its own task.
5. Implement Phases 7–12 in the dependency order in the table. Each owner writes its own
   `docs/phase-<n>-summary.md`, problem file and only its task-board row after its tests pass.

Short intent comments belong only beside the non-obvious rules: exact-selector check, replay
fingerprint comparison, redaction-before-persistence, deterministic tie-break and budget stop.
Comments must state *why* an invariant exists; naming and small methods should state *what*.

## Handoff to Critic

The critic should review this blueprint against the current source before developers act, with
these explicit questions:

1. Is preserving the current pre-receipt embedding invocation the correct extraction baseline, or
   should an approved semantic change establish receipt-before-embedding? If changed, which
   transaction/concurrency guarantees are required?
2. Do Phase 7 typed hot state, Phase 8 specialized decision command and Phase 9 migration ledger
   require a new Contracts major, or can their behaviour be expressed safely through current
   provider-neutral types?
3. Does the proposed project dependency graph keep MCP/Client/Migration free of direct LanceDB and
   keep Core free of SQLite/ASP.NET/AGM on reflection-based tests?
4. Do the listed characterization and conformance tests prove no authorization, redaction,
   idempotency, atomicity, token-budget or citation regression before a file split is accepted?

Until the critic resolves these points, developers may perform only no-semantic-delta extraction
backed by the listed characterization tests.
