# AGM memory: текущее состояние

> Владелец: `phase0_baseline`  
> Срез: 2026-08-08, исходный репозиторий `ai-group-manager` @ `a704d90`  
> Граница исследования: только чтение исходного модуля и его интеграций; реальные данные и SQLite-файлы не копировались.

## Summary

AGM уже выделил memory в три пакета (`Agm.Memory.Abstractions`, `Agm.Memory`, `Agm.Memory.Sqlite`), но runtime всё ещё зависит от SQLite-таблиц AGM. Есть две разные рабочие модели: один долгоживущий summary на workspace и временная fan-out память на конкретный run. Canonical ingestion и retrieval-контракты существуют как in-process/reference слой; durable canonical storage и реальные lexical/vector providers отсутствуют.

## SQLite-данные, границы и жизненный цикл

| Таблица | Ключ и содержимое | Граница/срок | Удаление и ответственный |
| --- | --- | --- | --- |
| `agm_memory_schema` | Версия модуля (`Agm.Memory`, сейчас `1`) | Не содержит memory content | `SqliteMemorySchema`; политики удаления нет |
| `workspace_memory_settings` | `workspace_id`, флаг `enabled`, версия настроек | Настройка workspace без TTL | `SetEnabledAsync`; `ForgetAsync` её не удаляет |
| `workspace_memory` | Один summary: content, reason, version, JSON provenance, timestamp | Только `workspace_id`; TTL нет | `ForgetAsync` удаляет summary при совпадении версии |
| `workspace_memory_candidates` | Очередь извлечённых кандидатов: workspace/chat/run/message/execution, content, попытки, lease/failure | Workspace + chat + optional run; TTL/автоматическая очистка не определены | observer отменяет pending при disable, помечает завершённые/ошибочные записи; физической purge-политики нет |
| `fanout_memory` | Аккумулированный content для run, version, timestamps, `expires_at` | `(workspace_id, parent_chat_id, run_id)`; API допускает TTL 1 мин.–24 ч., AGM пишет 4 ч. | `ClearAsync` либо lazy deletion при read/append после expiry |
| `fanout_memory_events` | `(run_id, event_key)` для идемпотентного capture | Живёт вместе с fan-out memory | каскадно удаляется с `fanout_memory` |

`SqliteMemorySchema.InitializeAsync` создаёт только module-owned таблицы и вызывается явно. В production AGM подключает эти таблицы к той же SQLite БД, что и `workspaces`, `agent_jobs` и `fanout_runs`.

## Рабочие потоки

```text
terminal agent job
  -> LocalWorkspaceMemoryExtractor (локальная фильтрация секретов)
  -> WorkspaceMemoryLifecycleObserver (AGM direct SQL)
  -> workspace_memory_candidates
  -> claim/retry/consolidate
  -> workspace_memory
  -> SqliteAgentJobService.ClaimNextAsync
  -> bounded workspace-memory prompt injection

fan-out task / previous output
  -> FanOutContextBuilder
  -> read current fanout_memory
  -> bounded context
  -> append compressed context with capture-key idempotency
  -> fanout_memory
```

### Workspace summary

- `WorkspaceMemoryLifecycleObserver` запускается после успешного terminal job. Он сам читает AGM `agent_jobs`, `todo_run_steps` и `fanout_members`, ставит кандидат в очередь и затем консолидирует его прямым SQL. Это основная runtime-связность, которую нужно убрать на write-path cutover.
- Кандидат ограничен 20 уникальными строками до 600 символов (максимум 4 000 символов). Observer консолидирует до 12 000 символов, хранитель допускает до 16 000; публичная запись также ограничивает reason 200 символами и provenance 100 элементами. Observer хранит последние 50 provenance по execution ID.
- Очередь использует lease 1 минуту, экспоненциальный retry до 300 секунд и переводит запись в failed после восьмой попытки. Это retry-семантика, а не retention-политика.
- При claim следующего job AGM получает `GetInjectionAsync(workspaceId)`, вкладывает не более 2 000 символов memory в prompt и затем применяет общий предел prompt 20 000 символов. При обрезании остаются начало и конец с omission marker.

### Fan-out memory

- `FanOutContextBuilder` читает memory только когда `MemoryEnabled`; затем при наличии предыдущего вывода записывает compressed append с TTL 4 часа.
- Хранилище принимает append до 4 000 символов, хранит максимум 16 000 и применяет optimistic version плюс capture key длиной до 128 безопасных ASCII-символов. Повтор того же capture key возвращает прежнее значение (`Unchanged`).
- TTL применяется лениво: просроченная строка физически удаляется при следующем read или append. Без такого обращения строка может остаться в файле SQLite после `expires_at`.

## API и семантика конкуренции

| Поверхность | Семантика |
| --- | --- |
| `IWorkspaceMemoryService.GetAsync` | Возвращает metadata; content только при `includeContent=true`; несуществующий workspace возвращает `null` |
| `SetEnabledAsync` | Optimistic concurrency по settings version; disable отменяет queued/leased candidates с кодом `WorkspaceMemoryDisabled` |
| `ForgetAsync` | Удаляет только `workspace_memory` при совпадении memory version; settings и candidates не удаляет |
| `IWorkspaceMemoryWriter.WriteAsync` | Проверяет workspace, enablement, version и provenance workspace; upsert с optimistic concurrency |
| HTTP `/api/v1/workspaces/{workspaceId}/memory` | GET (content opt-in), PUT `/settings`, DELETE; ETag — memory version или settings version; конфликты — HTTP 409 |
| `IFanOutMemoryStore` | `AppendAsync`, `ReadAsync`, `GetStatusAsync`, `ClearAsync`; read/status scoped к тройке ID, clear поддерживает optional expected version |
| HTTP `/api/v1/workspaces/{workspaceId}/fanout-runs/{runId}/memory` | Только безопасный status и clear; raw fan-out content через HTTP не выдаётся |

## Scope и авторизация

- Durable workspace service знает только `Guid workspaceId`. В AGM `AgmMemoryScopeValidator` проверяет наличие workspace в `workspaces`.
- Fan-out service требует полный `(workspaceId, parentChatId, runId)`; в AGM валидатор сверяет эту тройку с `fanout_runs`.
- Canonical domain использует другой `MemoryScope(Guid TenantId, Guid? ProjectId)`, а retrieval — ещё один, строковый `MemoryRetrievalScope(tenantId, projectId)`. Это подтверждает необходимость целевого единого scope, но соответствие AGM workspace/chat/run к tenant/project ещё не утверждено.
- Детерминированный retrieval отфильтровывает кандидаты не той tenant/project scope и superseded records, однако не исполняет реальный storage query.

## Redaction и privacy

- `LocalWorkspaceMemoryExtractor` до постановки кандидата удаляет именованные credential-поля, bearer/API/GitHub/JWT-подобные токены, private keys и approval payloads.
- `MemorySecretRedactor` повторно применяется в `SqliteWorkspaceMemoryService.WriteAsync` и `SqliteFanOutMemoryStore.AppendAsync`; он маскирует bearer и типовые `token/password/secret/api-key/authorization` пары. `FanOutContextBuilder` отдельно редактирует source/role/previous output.
- Это несколько локальных regex-гейтов, а не единый ingress-policy до любого persistence. Нельзя считать текущую фильтрацию доказательством полной защиты всех секретов.
- Telemetry-тесты отдельно требуют, чтобы trace receipt не содержал tenant, query или raw content.

## Retrieval, качество и что отсутствует

- `HybridMemoryRetrieval` выполняет детерминированный RRF над уже переданными lexical/vector кандидатами. Реальных `IVectorSearch`/`ILexicalSearch` портов и provider implementation нет.
- `MemoryContextBuilder` умеет provider-neutral reranking, lifecycle filtering, de-duplication, citations и token budget, но не является текущим prompt injection path для workspace summary.
- `MemoryGoldenCorpusEvaluator` измеряет supplied ranked hits: mean recall, NDCG, evidence coverage, exact-ID pass rate и p95 latency. Он не запускает SQLite, vector или lexical поиск.
- Исходный unit fixture содержит шесть безопасных synthetic query slices. Его неизменённая, versioned JSON-проекция находится в [`quality-corpus/agm-memory-stage-zero-v1.json`](quality-corpus/agm-memory-stage-zero-v1.json). Это coverage fixture, **не** baseline на реальных запросах или latency.

## Источники доказательств

- `modules/Agm.Memory/src/Agm.Memory.Sqlite/SqliteMemorySchema.cs`
- `modules/Agm.Memory/src/Agm.Memory.Sqlite/SqliteWorkspaceMemoryService.cs`
- `modules/Agm.Memory/src/Agm.Memory.Sqlite/SqliteFanOutMemoryStore.cs`
- `modules/Agm.Memory/src/Agm.Memory/LocalWorkspaceMemoryProcessing.cs`
- `modules/Agm.Memory/src/Agm.Memory/MemoryContentHelpers.cs`
- `modules/Agm.Memory/src/Agm.Memory/MemoryGoldenCorpusEvaluator.cs`
- `modules/Agm.Memory/tests/Agm.Memory.Tests/MemoryStageZeroTests.cs`
- `src/AiGroupManager.Infrastructure/Memory/WorkspaceMemoryLifecycleObserver.cs`
- `src/AiGroupManager.Infrastructure/Runners/SqliteAgentJobService.cs`
- `src/AiGroupManager.Infrastructure/FanOut/FanOutContextBuilder.cs`

Список решений, которые нельзя подменять этим исследованием, и точных продолжений находится в [`phase-0-open-problems.md`](phase-0-open-problems.md).
