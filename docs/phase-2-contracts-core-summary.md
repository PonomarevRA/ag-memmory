# Фаза 2 — Domain Model и Core: итог

> Владелец: `phase2_contracts_core`  
> Статус: локальная реализация и acceptance tests завершены 2026-08-08.  
> Плановый статус обновляет migration orchestrator; этот документ не заменяет task board.

## Что готово

Созданы независимые `AgMemory.Contracts` и `AgMemory.Core`. Это новая
provider-neutral основа платформы: она не открывает базу, не вызывает
embedding SDK и не знает об AGM, SQLite, Arrow, LanceDB, ASP.NET или MCP.
Compatibility-проекты не менялись.

### Domain Model (Phase 2)

- `MemoryScope` и `ScopeSelector` сравнивают ровно пять измерений
  (`tenant`, `project`, `workspace`, `chat`, `run`), включая `null`.
  В модели нет родительских scope или неявного расширения доступа.
- Введены opaque string value objects с нормализацией GUID, canonical
  `MemoryRecord`, `MemoryEntity`, `MemoryRelation`, `DecisionDetails`,
  `SessionHotMemory`, provenance/evidence и embedding metadata.
- Поддерживаются Fact, Observation, Decision, Constraint, Procedure,
  Preference, Incident, Outcome, Summary и остальные canonical record types.
- Команды и query DTO возвращают typed outcomes/`MemoryError`, а не раскрывают
  пользовательский content в тексте исключения или telemetry shape.
- `MemoryRecord.Reason` реализован как nullable, ingress-redacted public field.
  Это точная форма технического уточнения `TD-P2-01` для будущего optional
  legacy importer.

### Core use cases

- `MemoryCommandService` проводит remember/lifecycle/hot-memory/forget через
  порядок: validation → authorization → redaction → canonicalization/dedup →
  optional explicitly configured embedding → одна store transaction с receipt
  и content-free outbox message.
- Идемпотентность ограничена command kind + exact authorized selector + key;
  reuse ключа с другим fingerprint отклоняется. Existing record/hot-memory
  writes используют optimistic `ExpectedVersion`; `0` означает create-only.
- `ForgetAsync` остаётся fail-closed без утверждённой retention policy.
  Vector embedding также не имеет default provider/model и требует explicit
  embedding contract.
- `MemoryQueryService` authorizes до обращения к store/search/graph ports,
  повторно фильтрует exact scope, active status и expiry, детерминированно
  объединяет lexical/vector candidates RRF (`k=60`, versioned) и строит
  cited context в строгом token budget. Eligible hot memory читается только
  в exact session scope; Summary fallback также cited и budgeted.

## Граница с Phase 3 Storage Abstraction

`src/AgMemory.Contracts/Ports.cs` уже объявляет provider-neutral
`IMemoryStore`/transaction, `IVectorSearch`, `ILexicalSearch`, `IMemoryGraph`,
authorization, redaction, embedding, retention и outbox ports. Это контракт,
которому должен соответствовать storage owner; это **не** durable adapter.

Единственный in-memory store находится в
`tests/AgMemory.Core.Tests/TestKit.cs`. Он нужен только чтобы доказать
transaction/idempotency/concurrency/scoping в Core tests и не попадает в
runtime assembly. Phase 3 остаётся владельцем production storage behaviour,
adapter-level failure/reopen/concurrency tests и любых LanceDB/Arrow
references.

## Проверка

Выполнено из корня workspace:

```bash
dotnet build ag-memory.slnx --no-restore
dotnet test tests/AgMemory.Contracts.Tests/AgMemory.Contracts.Tests.csproj --no-restore
dotnet test tests/AgMemory.Core.Tests/AgMemory.Core.Tests.csproj --no-restore
dotnet test tests/AgMemory.IntegrationTests/AgMemory.IntegrationTests.csproj --no-restore
dotnet test tests/Compatibility/Agm.Memory.Tests/Agm.Memory.Tests.csproj --no-restore
```

Результат новой области: 3 Contracts tests, 12 Core tests и 1 integration
test — все passed. Дополнительно regression compatibility suite: 55 passed.
Новые tests покрывают validation/value objects, exact-scope isolation, denied
request without port calls, idempotency, safe redaction, optimistic versions,
policy fail-closed behaviour, embedding mismatch, RRF tie break, strict cited
context budget, expiry и public API dependency firewall.

## Изменённые артефакты

- `src/AgMemory.Contracts/` — BCL-only public contracts and ports.
- `src/AgMemory.Core/` — Contracts-only command/query orchestration and RRF.
- `tests/AgMemory.Contracts.Tests/`, `tests/AgMemory.Core.Tests/`,
  `tests/AgMemory.IntegrationTests/` — acceptance fixtures and tests.
- `ag-memory.slnx` — registration новых projects.

## Handoff

Storage owner получает public ports и exact-scope/filtering invariants из
`AgMemory.Contracts`. QA может начать с `CoreQueryTests` firewall/isolation
cases и `ScopedCommandIntegrationTests`. Открытые вопросы, которые Core не
должен решать сам, перечислены в
[phase-2-open-problems.md](phase-2-open-problems.md).
