# AGM → LanceDB Memory Platform: план реализации

> **Статус:** активен, пересмотрен 2026-08-08 по новым вводным владельца.  
> **Принцип:** фазы выполняются как отдельные задачи с одним владельцем, human-ready summary и отдельной фиксацией проблем. Сводный прогресс — в [task board](memory/task-board.md), открытые вопросы — в [реестре проблем](memory-migration-problems.md).

## Цель

Вынести память AGM в самостоятельную платформу с LanceDB за storage abstraction. AGM получает только .NET Memory Client/модуль и не зависит от конкретной базы. Legacy API/package compatibility и сохранение существующих AGM данных **не являются gates**: после controlled rollout владелец может удалить старые данные. При этом необязательный importer остаётся безопасным, идемпотентным и проверяемым.

## Неподвижные правила

- Business logic/Domain не используют LanceDB или Arrow types.
- Не сохраняются conversations, raw logs и chain-of-thought; хранятся только компактные reusable facts, references и decision trace.
- Каждый durable record содержит scope, importance, confidence, estimated token cost и provenance.
- Scope/status/expiry фильтруются до ранжирования; контекст строго ограничен budget и содержит citations.
- Каждая команда авторизуется, редактируется до persistence и идемпотентна.
- External rollout, production import и destructive deletion требуют отдельного approval; локальная реализация не выдаёт их за выполненные.

## Target architecture

```text
AGM / services ──> AgMemory.Client ──> Memory Domain + Retrieval + Hot/Decision/Graph
                                                │
                                         Storage abstraction
                                                │
                                      AgMemory.Storage.LanceDb
```

## Фазы и принимаемые результаты

### 1. Current AGM Memory Analysis — **локально завершена**

Документировать entities, SQLite storage model, retrieval/prompt flow, ограничения и migration risks в `docs/memory/current-state.md`.

- [x] Исследован исходный AGM module без его изменения.
- [x] Зафиксированы SQLite tables, direct-SQL coupling, scopes, TTL/redaction и synthetic quality corpus.
- [ ] Снять production snapshot или real-query baseline — только после data-owner approval.

**Результат:** [current-state](memory/current-state.md), [baseline notes](phase-0-baseline-notes.md).

### 2. Memory Domain Model — **локально завершена**

Реализовать `Memory`, `MemoryScope`, `MemoryEntity`, `MemoryRelation`, `Decision`, `Outcome`, `SessionMemory`, а также типы Fact/Observation/Decision/Constraint/Procedure/Preference/Incident/Outcome/Summary.

- [x] Архитектурный контракт и implementation brief подготовлены.
- [x] Собрать domain project и unit/integration tests.
- [x] Закрыть local acceptance (exact scope, provenance, lifecycle, no prohibited raw content).

**Результат:** `AgMemory.Contracts`, `AgMemory.Core`, [contract](contracts.md).

### 3. Storage Abstraction — **локально завершена**

Выделить независимые `IMemoryStore`, `IVectorSearch`, `ILexicalSearch`, `IMemoryGraph` и проверить отсутствующие reverse dependencies.

- [x] Provider-neutral boundary спроектирована.
- [x] Реализовать и проверить public ports на готовом domain model отдельной задачей.

### 4. LanceDB Adapter — **локально завершён на macOS**

Создать `AgMemory.Storage.LanceDb`: schema mapping, persistence, vector/lexical search, metadata filters и batch operations. Только этот project может ссылаться на LanceDB/Arrow.

- [x] macOS arm64 spike доказал create/open/reopen, Arrow mapping, batch/upsert, filter, vector search и delete на LanceDB 2.5.0.
- [x] Реализовать production adapter и integration suite.
- [ ] Проверить Linux x64, concurrency, FTS/RRF, indexes/rebuild и schema evolution.

**Результат:** [spike report](phase-3-lancedb-spike.md), `AgMemory.Storage.LanceDb`.

### 5. Initial Memory Schema — **локально завершена**

Закрепить LanceDB schema для `id`, `scope`, `project`, `type`, `status`, `canonical_text`, `importance`, `confidence`, `created_at`, `updated_at`, `entities`, `embedding`, `provenance` с версиями schema и embedding contract.

- [x] Versioned schema manifest, fingerprint и embedding identity/dimension validation реализованы.

### 6. Hybrid Retrieval — **локально завершена**

Реализовать pipeline `Hot → metadata filter → vector + lexical → deterministic RRF → duplicate removal → Context Builder` с budget-aware minimal cited context.

- [x] Pipeline extracted into `AgMemory.Core/Retrieval`, including independent provider fallback, deterministic dedupe and cited budget packing.

### 7. Hot Memory — **локально завершена**

Реализовать bounded TTL `SessionHotMemory`: current goal, active entities, recent decisions, open questions и working facts, включая decay/promotion policy.

- [x] Structured state, bounded TTL, deterministic decay and promotion eligibility are implemented in `AgMemory.Core/HotMemory`.

### 8. Decision Memory — **локально завершена**

Реализовать compact decision trace: Problem, Context, Options, Decision, Reason, Consequences, Outcome. Chain-of-thought не принимается и не хранится.

- [x] Dedicated compact trace validator/service uses the central authorized and redacted command pipeline.

### 9. Existing Memory Migration — **ожидает adapter**

Добавить snapshot/export CLI: converter → canonical memory → LanceDB import → manifest/validation. Поддержать dry-run, resumable batches, idempotency, count/parity/duplicate/missing-data validation. Это optional migration path, а не prerequisite новой установки.

### 10. Agent Integration — **ожидает client/core**

Добавить `.NET` client и MCP tools: `memory_recall`, `memory_search`, `memory_remember`, `memory_record_decision`. У обоих transport одинаковые scope, authorization, idempotency и results.

### 11. Token Economy — **ожидает core/retrieval**

Проверить ingress/normalization/deduplication и context builder: no conversation/raw logs, references preferred, all records have importance/confidence/token cost, decisions remain compact.

### 12. Validation — **ожидает queryable adapter**

Создать `MemoryMigrationBenchmark`; сравнивать old fixture/AGM export и LanceDB по Recall@5, Recall@20, MRR, p50/p95, context tokens, duplicate rate. До real corpus это только synthetic evidence, не production quality gate.

## Definition of done

- [ ] AGM/services используют `AgMemory.Client`/модуль, а не provider implementation.
- [ ] LanceDB adapter поддерживает durable writes, scoped vector/lexical retrieval и metadata filters.
- [ ] Optional legacy import проходит parity и replay validation на approved snapshot.
- [ ] Benchmark показывает equal-or-better retrieval и approved context reduction.
- [ ] Decision/hot memory и MCP/.NET client имеют одинаковую authorisation semantics.
- [ ] Ни Domain, ни business logic не зависят от LanceDB.

## Историческая заметка

Предыдущая более широкая версия плана требовала compatibility packages и обязательный cross-repository AGM cutover. После уточнения владельца она заменена этим документом; уже созданный compatibility слой сохранён как временный технический asset, но не блокирует фазы 2–12.
