# Контракты AgMemory

> **Contract card**
>
> - **Владелец:** `architecture` → реализация `contracts/core owner` в Phase 2.
> - **Статус:** proposed public contract. Код должен сохранять эти инварианты; P0 decisions остаются внешней policy.
> - **Совместимость:** breaking change означает новый major Contracts/Client package. Payload и benchmark обязаны нести `contract_version`.

## Граница public API

`AgMemory.Contracts` содержит DTO, value objects, интерфейсы портов, validation errors и объяснения результата. Он не должен раскрывать LanceDB/Arrow/SQLite/ASP.NET типы. `AgMemory.Core` реализует use cases, RRF, lifecycle, deduplication и context budget только через эти порты. `AgMemory.Client` — тонкий facade над командами и запросами, а не отдельная бизнес-реализация.

Ниже приведён нормативный shape. Имена допустимо скорректировать при реализации, если не меняются семантика, поля, scope safety и versioning.

```csharp
public sealed record MemoryScope(
    ScopeId TenantId,
    ScopeId? ProjectId,
    ScopeId? WorkspaceId,
    ScopeId? ChatId,
    ScopeId? RunId);

public sealed record ScopeSelector(MemoryScope Scope); // exact match only
public sealed record AuthorizedScopeSet(IReadOnlyList<ScopeSelector> Selectors);

public sealed record MemoryRecord(
    MemoryId Id,
    MemoryScope Scope,
    MemoryRecordType Type,
    MemoryLifecycleStatus Status,
    string CanonicalText,
    double Importance,
    double Confidence,
    int EstimatedTokenCost,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    IReadOnlyList<string> Entities,
    MemoryProvenance Provenance,
    EmbeddingReference? Embedding,
    DateTimeOffset? ExpiresAt,
    string DeduplicationKey);
```

`ScopeId` и `MemoryId` — opaque non-empty value objects, сериализуемые как строки. Им нельзя приписывать security semantics по format; текущие AGM GUID сохраняются как нормализованные GUID strings. Это не меняет P0-01: владелец policy определяет, как legacy workspace/chat/run дополняются tenant/project и кто получает selectors.

### Scope invariants

1. `TenantId` обязателен для durable record. Каждый заданный ID непуст; неизвестные/неавторизованные combinations отклоняет `IAuthorizationScopeValidator`.
2. `MemoryScope` описывает место хранения. `AuthorizedScopeSet` описывает только scope, разрешённые для данной операции. Клиент не может превратить произвольный input в authorised selector.
3. `ScopeSelector` сравнивает все пять измерений exactly, включая `null`. Core не делает implicit widening/narrowing.
4. Чтобы session read увидел workspace summary, validator выдаёт второй explicit workspace selector. Это policy decision с audit-safe reason, не эвристика adapter-а.
5. Все `IMemoryStore`, `IVectorSearch`, `ILexicalSearch`, `IMemoryGraph` и hot-memory вызовы принимают `AuthorizedScopeSet`; фильтрация scope, status и expiry выполняется до candidate ranking.
6. Тесты должны включать negative cases: тот же workspace с иным tenant; тот же chat с иным run; аналогичный ID в неразрешённом selector. В результате нет record, evidence ID или цитаты.

## Canonical data

| Тип | Назначение и обязательные детали |
| --- | --- |
| `Fact`, `Preference`, `Task`, `Event`, `Procedure`, `Constraint`, `Incident`, `LessonLearned` | переносимые canonical records из AGM; содержат record envelope и provenance |
| `Observation` | наблюдение с confidence, не выдаётся за факт без lifecycle policy |
| `Outcome` | измеренный результат, с ссылкой на subject/decision если есть |
| `Summary` | компактный scope summary; может быть single-summary fallback |
| `Decision` | record envelope плюс `DecisionDetails` ниже |
| `SessionHotMemory` | bounded, session-scoped, expiring context; не является бессрочным canonical summary |

```csharp
public sealed record DecisionDetails(
    string Problem,
    string Context,
    IReadOnlyList<string> Options,
    string Decision,
    string Reason,
    IReadOnlyList<string> Consequences,
    string? Outcome);

public sealed record MemoryProvenance(
    string SourceSystem,
    string? LegacyRecordId,
    ScopeId? WorkspaceId,
    ScopeId? ChatId,
    ScopeId? RunId,
    string? MessageId,
    string? ExecutionId,
    IReadOnlyList<SourceEvidenceRef> Evidence);

public sealed record EmbeddingReference(
    string Provider, string Model, string ModelVersion,
    int Dimension, string Normalization, string ContentHash);
```

`MemoryProvenance` сохраняет workspace/chat/run/message/execution и source fragment/evidence identity. `LegacyRecordId` — traceability key, а не permission key. Content длинных source fragments предпочтительно остаётся во внешнем source system; record хранит stable evidence reference и минимально нужный approved fragment. Нельзя сохранять chain-of-thought, полные conversations или raw logs.

`EmbeddingReference` обязателен для vector-searchable record и nullable для record, который ещё не embedded/не должен участвовать в vector search. Конкретные provider/model/dimension/normalization/re-embedding rules намеренно не зафиксированы до P0-03. Adapter отклоняет смешение несовместимых embedding contracts в одном query/index либо явно partition-ит их и отражает version в response/report.

### Lifecycle, deletion и expiry

Контракт переносит известные состояния AGM: `Draft`, `Active`, `Superseded`, `Invalid`. `LifecycleCommand` меняет состояние с `ExpectedVersion`; Core возвращает typed `Applied`, `NotFound`, `StaleVersion` или `InvalidTransition`. Только `Active`, неистёкшие records по умолчанию участвуют в recall.

Delete/forget — отдельная command семантика, а не неявная смена state. Решение P0-02 определит physical purge против tombstone, retention, legal hold и каскад evidence/audit. До него интерфейс обязан возвращать `PolicyNotConfigured` для операции, которая потребовала бы такой необратимой policy; migration не имеет права угадывать её.

`SessionHotMemory` обязан иметь `ExpiresAt`; для legacy fan-out допустим только диапазон TTL, подтверждённый текущим API (1 минута–24 часа). Текущее применение 4 часов — наблюдаемое compatibility значение, не глобальная retention policy. Expired data не возвращается; физическое purge/ janitor определяется P0-02.

## Команды, запросы и порты

```csharp
public interface IMemoryCommandService
{
    Task<RememberResult> RememberAsync(RememberCommand command, CancellationToken ct);
    Task<LifecycleResult> ApplyLifecycleAsync(LifecycleCommand command, CancellationToken ct);
    Task<HotMemoryResult> AppendHotMemoryAsync(AppendHotMemoryCommand command, CancellationToken ct);
}

public interface IMemoryQueryService
{
    Task<MemorySearchResult> SearchAsync(MemorySearchRequest request, CancellationToken ct);
    Task<MemoryContext> BuildContextAsync(MemoryContextRequest request, CancellationToken ct);
    Task<SessionHotMemory?> ReadHotMemoryAsync(HotMemoryReadRequest request, CancellationToken ct);
}

public interface IMemoryStore { /* transaction, get/list/upsert, lifecycle, batch import */ }
public interface IVectorSearch { /* authorised semantic candidates with rank + score */ }
public interface ILexicalSearch { /* authorised lexical candidates with rank + score */ }
public interface IMemoryGraph { /* authorised relations and graph candidates */ }
public interface IEmbeddingProvider { /* versioned embedding creation */ }
public interface IAuthorizationScopeValidator { /* requested scope -> AuthorizedScopeSet or denial */ }
public interface IIngressRedactor { /* redact/reject before any persistence */ }
public interface IClock { DateTimeOffset UtcNow { get; } }
public interface IIdGenerator { MemoryId NewId(); }
public interface ICommandReceiver { /* idempotent command-envelope ingress */ }
public interface IOutbox { /* durable delivery acknowledgement */ }
```

Public implementation detail is deliberately omitted from `IMemoryStore`: consumers use command/query services, not storage. Adapter-specific batch and transaction implementations may add internal interfaces. This prevents a client, MCP tool or migration caller from bypassing authorization/redaction/consistency policy.

### Command envelope invariants

Every mutating command includes `CommandId`, `IdempotencyKey`, `Actor`, `CorrelationId`, requested `MemoryScope`, and an explicit contract version. `IdempotencyKey` has a unique scope within command kind + authorised location scope; the first durable outcome is returned for an exact replay. Reusing a key for a different canonical payload fails deterministically rather than overwriting data.

`ExpectedVersion` is mandatory for mutation of an existing record or hot-memory entry. `0` means create-if-absent only; a conflict is visible to the caller. Outbox delivery uses the same command ID/key, so retry after transport failure cannot duplicate record, evidence or audit entry.

Ingress order is normative:

```text
validate envelope -> authorise scope -> redact/reject -> canonicalise -> deduplicate
                  -> calculate token cost -> embed -> transactional store/outbox write
```

No service may persist, enqueue or log unredacted command content before `IIngressRedactor` succeeds. A redaction decision is recorded only as rule/version and counts, never with secret/plaintext content.

### Search and context invariants

`MemorySearchRequest` includes authorised selectors, type/status filters, query text or embedding reference, `Limit`, and retrieval configuration version. Vector and lexical ports return provider scores plus **positive provider rank**. Core runs deterministic RRF over those candidates, records per-source contributions, removes duplicate/superseded/expired records, then invokes optional graph/context reranking.

`MemoryContextRequest` has an explicit `TokenBudget` and citation requirement. The builder returns `MemoryContext` containing only selected text, citations (`MemoryId` + evidence IDs), token estimate, omitted count and retrieval/config versions. It must not exceed the budget. An empty search may invoke an explicit, eligible single-summary fallback; it must still be redacted, cited and budgeted.

## Client and MCP parity

`AgMemory.Client` exposes four stable capability groups that map one-to-one to the platform:

| Capability | .NET client | MCP tool in Phase 7 | Required equivalence |
| --- | --- | --- | --- |
| recall/context | `RecallAsync` | `memory_recall` | same authorised selectors, citations, token budget |
| search | `SearchAsync` | `memory_search` | same filters, ordering, explanation version |
| remember | `RememberAsync` | `memory_remember` | same ingress, idempotency, errors |
| decision | `RecordDecisionAsync` | `memory_record_decision` | same `DecisionDetails`, scope and lifecycle |

Transport may shape errors, but cannot turn an authorization denial into an empty-success response or give MCP a broader scope than the .NET client. Contract tests execute the same fixtures through both facades.

## Implementation acceptance checklist

- Contracts/Core compile without storage, provider, ASP.NET or AGM dependencies.
- Scope validator tests prove no implicit broadening and no cross-scope evidence/citations.
- Replay of an identical command is idempotent; altered payload with reused key is rejected.
- Redaction negative fixtures prove no raw secret in store/outbox/log test sink.
- Context builder is deterministic for identical input/config and never exceeds budget.
- RRF result contains ranks/contributions/config version and stable tie breaking.
- Legacy workspace summary and fan-out data fit `Summary`/`SessionHotMemory` shapes without losing provenance, version, timestamps or expiry.

## Decisions that this contract cannot make

The following are implementation blockers, not implicit defaults: legacy-to-tenant/project mapping and authority (P0-01); retention/delete/legal hold (P0-02); embedding compatibility policy (P0-03); package/private feed/pinning (P0-04); and `mcp-ai-memory` convergence (P0-05). They must be approved and recorded in `docs/phase-0-open-problems.md` (or its approved successor) before their corresponding data or rollout path is enabled.
