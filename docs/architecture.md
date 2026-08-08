# Архитектура AgMemory

> **Architecture card**
>
> - **Владелец:** `architecture`
> - **Статус:** proposed — достаточно для реализации технических фаз, но не заменяет решения P0-01…P0-05.
> - **Цель:** вынести memory из AGM в самостоятельную платформу с опубликованным клиентом и изолированным LanceDB-адаптером.
> - **Источники:** [план миграции](memory-migration-plan.md), [baseline AGM](current-state-agm.md), исходники `../ai-group-manager/modules/Agm.Memory` и `../ai-group-manager/src/AiGroupManager.Infrastructure/Memory/WorkspaceMemoryLifecycleObserver.cs` (проверены 2026-08-08).
> - **Не делает:** не утверждает продуктовую модель scope, retention, embedding provider, NuGet feed или судьбу `mcp-ai-memory`.

## Коротко

AgMemory — отдельная memory-платформа. AGM получает только опубликованные пакеты `AgMemory.Contracts` и `AgMemory.Client`; ни исходники, ни SQLite-схема AGM не являются её runtime-зависимостью. Домен и Core знают только версии контрактов и порты. LanceDB, Arrow, SQLite, ASP.NET и типы AGM остаются за внешней границей.

Это устраняет текущую связность: в AGM `WorkspaceMemoryLifecycleObserver` одновременно читает `agent_jobs`/`todo_run_steps`/`fanout_members` и пишет `workspace_memory*`; `FanOutContextBuilder` использует общий SQLite store. В новой архитектуре события и команды пересекают опубликованную границу, а не таблицы.

## Зависимости и запреты

Стрелка означает ссылку проекта (`A --> B` означает «A зависит от B»).

```text
                         +-------------------------------+
                         | AGM (отдельный репозиторий)    |
                         | PackageReference, pinned      |
                         +---------------+---------------+
                                         |
                                 AgMemory.Client
                                         |
                                         v
+-------------------+       +-------------------+       +-------------------+
| AgMemory.McpServer| ----> | AgMemory.Contracts | <---- | AgMemory.Core     |
| (transport only)  |       | DTOs, ports, rules |       | use-cases/RRF     |
+--------+----------+       +-------------------+       +---------+---------+
         |                                                        |
         |                                                        | implements ports
         |                                                        v
         |                                             +---------------------+
         +-------------------------------------------> | Storage.LanceDb     |
                                                       | LanceDB + Arrow only |
                                                       +---------------------+

AgMemory.Migration --> Contracts + Core + Storage.LanceDb + Storage.Sqlite.Legacy
AgMemory.Storage.Sqlite.Legacy --> Contracts             (import/export/rollback tooling only)
```

`AgMemory.Client` предоставляет потребителю команды, запросы и DTO; он не открывает LanceDB и не передаёт SQL. `AgMemory.McpServer` вызывает тот же client/command-query facade, что и .NET-клиент. Сборка composition root может находиться в host-проекте, но не в Contracts/Core.

| Компонент | Разрешённые зависимости | Явно запрещено |
| --- | --- | --- |
| `AgMemory.Contracts` | BCL и совместимые сериализационные примитивы | LanceDB, Arrow, SQLite, ASP.NET, DI host-типы, AGM |
| `AgMemory.Core` | `AgMemory.Contracts`, BCL | любой storage/provider, ASP.NET, AGM |
| `AgMemory.Client` | `AgMemory.Contracts` | storage/provider и локальные пути к репозиторию |
| `AgMemory.Storage.LanceDb` | Contracts/Core только для реализации портов | типы AGM; экспорт LanceDB/Arrow в public API |
| `AgMemory.Storage.Sqlite.Legacy` | Contracts и библиотека SQLite | runtime-регистрация после Phase 8 |
| `AgMemory.Migration` | опубликованные AgMemory-пакеты и экспорт/snapshot | `ProjectReference` на AGM |
| `AgMemory.McpServer` | Client/Contracts и MCP transport | прямой доступ к LanceDB или legacy SQLite |

**Обязательная проверка:** contract/architecture-тесты должны обходить public API Contracts/Core и падать, если там появляется namespace/assembly LanceDB, Arrow, SQLite, ASP.NET или `Agm.*`. Пакетные integration-тесты подтверждают, что AGM-потребитель собирается только с версиями выпущенных пакетов.

## Scope и доступ

Целевая модель хранит один `MemoryScope` с пятью именованными измерениями: `tenant`, `project`, `workspace`, `chat`, `run`. Каждое значение — непрозрачный непустой стабильный идентификатор; GUID из AGM передаются в канонической строковой форме. `tenant` обязателен для durable memory, остальные измерения могут отсутствовать. Эта форма — технический носитель; она **не утверждает**, какой AGM workspace принадлежит какому tenant/project и какие сочетания разрешены.

У record есть *location scope*. У read-запроса есть *authorised selectors*, выданные `IAuthorizationScopeValidator` после проверки caller и requested scope. Storage получает только selectors, созданные Core после такой проверки.

```text
caller + requested scope
          |
          v
IAuthorizationScopeValidator ---- deny ---> no query / audit-safe denial
          |
          v
authorised exact scope selectors --> hot / lexical / vector / graph ports
```

Технический безопасный дефолт до решения P0-01:

- selector совпадает **точно** по каждому указанному измерению;
- Core не расширяет `run` до `chat`, `workspace`, `project` или `tenant` сам;
- более широкий scope можно читать только если validator вернул отдельный явный selector;
- любой порт фильтрует scope, lifecycle status и expiry до ранжирования, а не после него.

Так workspace-summary остаётся доступным для session-подсказки только через явный workspace selector, а не через неявный обход scope. P0-01 должен утвердить cardinality, обязательность измерений и источник авторизации; до этого реальная миграция и tenant canary заблокированы.

## Данные и поток выполнения

```text
approved source/event or client command
  -> command envelope (actor, correlation, idempotency key, requested scope)
  -> authorize + validate scope
  -> ingress redaction
  -> extract/normalise + deduplicate + estimate tokens
  -> embed (versioned embedding contract)
  -> durable IMemoryStore transaction + outbox acknowledgement
  -> records, provenance/evidence, lifecycle/audit metadata

authorised recall
  -> exact SessionHotMemory for session selector
  -> mandatory filters (scope, status, expiry, type)
  -> lexical and vector searches independently
  -> deterministic RRF -> duplicate/supersession filtering -> graph/context rerank
  -> bounded cited context, or one eligible workspace-summary fallback
```

Redaction происходит перед **любой** записью в durable storage, outbox payload или диагностический event. Логи, trace и метрики содержат только opaque IDs, версии, счётчики и безопасные причины; они не содержат query, tenant или raw memory content. Текущие distributed regular-expression gates AGM являются входным фактом, но не доказательством такой гарантии.

Для RRF сохраняются provider ranks, фиксированный порядок tie-break (`record id`), конфигурация RRF и версия reranker в result explanation и benchmark report. Технический compatibility default — reciprocal-rank constant `60`, как в текущем `HybridMemoryRetrieval`; изменение требует новой конфигурационной версии и повторного benchmark, а не скрытой смены поведения.

## Инкрементальная граница совместимости

| Фаза | Что появляется в standalone workspace | Что по-прежнему обслуживает AGM | Безопасная граница / gate |
| --- | --- | --- | --- |
| 1 | перенесённые пакеты и compatibility packages `Agm.Memory.*` | текущий SQLite runtime | package-only consumption; нет `../ag-memmory` reference |
| 2 | versioned Contracts/Core и ingress policy | SQLite serving path | no reverse dependencies, scope/idempotency fixture |
| 3 | LanceDB adapter за портами | SQLite serving path | macOS + Linux spike и adapter integration suite |
| 4 | snapshot/export migration CLI и manifests | production reads/writes | dry-run, resume, validation-only; нет source reference AGM |
| 5 | query executor + benchmark/shadow reporter | SQLite определяет prompt | shadow не меняет prompt и не пишет исходные таблицы |
| 6 | Client read facade и flags | SQLite fallback | feature flag default `off`; rollout только после quality/SLO gate |
| 7 | command receiver/outbox, hot/decision capabilities | старый observer до переключения | новые записи через commands, idempotency и authorisation |
| 8 | read-only legacy tooling | ничего после approved rollback window | archive manifest до удаления legacy runtime |

Feature flags и fallback — только control plane. Fallback возвращает единственный eligible workspace summary, если новый retrieval пуст; он не требует минимального числа candidates и всегда соблюдает prompt/context budget. Переключение flag не должно выполнять миграцию, schema change или destructive delete.

## Операционные контроли и acceptance metrics

| Контроль | Что измеряем | Обязательный gate |
| --- | --- | --- |
| Scope safety | denied requests, candidate count outside authorised selectors, negative isolation tests | `0` cross-scope candidates в integration tests; canary не проходит при leakage |
| Command safety | idempotency hit/duplicate, concurrency conflict, redaction rejects | повтор одного envelope не создаёт второй record/evidence; raw payload отсутствует в telemetry fixtures |
| Migration safety | source/target counts, checksum parity, skipped/errors, ID map, elapsed time | каждая разница объяснена manifest policy; failed row replayable |
| Retrieval quality | Recall@5/@20, MRR, evidence/exact-ID rate, context tokens, duplicate rate | quality не хуже утверждённого baseline; exact-ID/evidence regressions `0` |
| Runtime quality | p50/p95 per path, empty-result fallback, error/timeout rate | p95 не выше утверждённого SLO; fallback bounded and cited |
| Durability | reopen/restart, index rebuild, expiry/lifecycle filters, batch upsert | adapter suite проходит на macOS и Linux |

Числовые production SLO, retention windows, legal-hold поведение, модель/dimension embedding и baseline на реальных данных пока не подставляются: это P0-02/P0-03 и P1-01. До их утверждения допустимы только synthetic/isolated tests и shadow reports без product rollout.

## Решения, дефолты и открытые вопросы

| Категория | Состояние | Содержание |
| --- | --- | --- |
| Technical decision | proposed | hexagonal dependencies и package-only AGM boundary; Core/Contracts storage-neutral; legacy SQLite только tooling |
| Technical default | proposed | exact selector matching, flag `off` до gate, RRF `k=60`, deterministic tie-break, one-summary fallback |
| Product/security decision | **pending P0-01** | scope mapping, hierarchy, authorisation source |
| Privacy/data decision | **pending P0-02** | retention, purge/tombstone, legal hold, candidate/event eligibility |
| Platform/ML decision | **pending P0-03** | provider, model, dimension, normalization, re-embedding policy |
| Release decision | **pending P0-04** | package identities, private feed, pin/rollback policy |
| Product/architecture decision | **pending P0-05** | `mcp-ai-memory`: separate product or documented integration path |

Ни один пункт со статусом pending не считается утверждённым данным документом. Реализация должна выражать его конфигурацией или fail-fast validation, а не угадывать policy.

## Разделение ответственности по следующим фазам

| Следующий владелец | Фазы | Получает от архитектуры | Результат |
| --- | --- | --- | --- |
| extraction/package owner | 1 | dependency rules, compatibility boundary | independently packed compatibility artifacts |
| contracts/core owner | 2 | [contracts.md](contracts.md), scope invariants | provider-neutral API and tests |
| storage/spike owner | 3 | adapter boundary and operational gates | LanceDB proof and adapter |
| migration owner | 4 | [migration-from-agm.md](migration-from-agm.md) | resumable import plus manifest |
| analyst/quality owner | 5 | quality metrics, corpus/report contract | measured shadow comparison and gate recommendation |
| client/cutover owner | 6 | fallback and flag rules | read canary/rollback control |
| ingestion/MCP owner | 7 | command/outbox and client parity | authorised write path and MCP parity |
| decommission owner | 8 | archive and rollback prerequisites | legacy removal evidence |

## Handoff: Analyst

- **Objective:** turn the existing synthetic corpus and the contracts below into a Phase-5 measurement specification; do not claim a real-data baseline.
- **Inputs:** this document, [contracts.md](contracts.md), [migration-from-agm.md](migration-from-agm.md), `docs/quality-corpus/agm-memory-stage-zero-v1.json`, and `docs/phase-0-open-problems.md`.
- **Deliverables:** an executable JSON report schema, slice definitions, comparison algorithm for legacy-versus-shadow result/evidence/context/latency, and a list of metrics blocked by P0 decisions.
- **Constraints:** no production data, no AGM edits, no invented SLO or embedding policy; label synthetic evidence as synthetic.
