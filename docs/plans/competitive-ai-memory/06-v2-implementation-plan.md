# Competitive AI Memory — план V2

**Статус:** ready for implementation после Wave 0; Q7 остаётся human gate только для выравнивания Web ↔ MCP.

**Заменяет как план реализации:** волны и work-packages A–D первой версии. Исходная сравнительная часть и [заметка о BCS RPA](05-bcs-rpa-connection.md) остаются источниками контекста.

## Цель

Улучшить полезность памяти без изменения глобальной архитектуры:

- chat `Event` не пересобирает и не засоряет compiled wiki;
- в контекст Yuki попадают только устойчивые типы в едином ограниченном бюджете;
- inbox и compiled wiki являются разными серверными проекциями одной памяти;
- Web и MCP либо доказуемо используют одну exact-scope память, либо явно обозначены как два store.

## Рамки V2

| Категория | Подтверждённый факт / решение |
| --- | --- |
| Store и контракты | Сохраняются LanceDB, `AgMemory.*` Contracts/Core и exact-scope authorization. |
| HTTP и MCP | Не добавляются HTTP routes и MCP tools; MCP surface остаётся `memory_recall`, `memory_remember`, `memory_status`. |
| Browser privacy | Browser не получает memory text, storage path, actor, scope, entity names или persistent IDs. |
| Модели и Markov | Статья BCS RPA не задаёт model requirement. Semantic embeddings, ASR/OCR, RRF tuning и legacy random-walk/Markov reranking не входят в V2. |
| Действующий runtime | Local Web/MCP используют `lexical-fallback`, а legacy `Compatibility/Agm.Memory.*` не является активной композицией standalone Web/MCP. |

### Не входит в V2

- замена LanceDB, git-md primary store, production auth или multi-writer;
- новые MCP tools, `memory_write_page`, `handoff_accept`, новые browser payloads;
- type-priority ranking, entity boost, vector/RRF tuning;
- подключение модели распознавания или векторизации;
- включение legacy graph walk / цепей Маркова в `AgMemory.*`.

## Факты, решения и открытый gate

| Вид | Содержание |
| --- | --- |
| Факт | Catalog source читается постранично, затем фильтруется; курсор сейчас зависит от оставшихся записей. Исключение Event без коррекции обхода может пропустить последующие records. |
| Факт | Текущий SPA home вызывает catalog, а не tree API. |
| Факт | `readerAligned` проверяет только Web Reader ↔ Web Graph; он не видит MCP process environment. |
| Решение V2 | Pagination всегда продвигается по числу сырых прочитанных строк (либо Event исключается в storage predicate до `Limit`); результат фильтра никогда не определяет source offset. |
| Решение V2 | `Event` не является ни inbox, ни compiled tree document, даже при случайно существующей wiki metadata. |
| Решение V2 | Для разделения inbox/tree используется server-side metadata predicate, а не UI filter. |
| Gate Q7 | Только человек с доступом к Web и MCP server/env подтверждает нормализованный storage path и весь exact scope. Q7=no — допустимый, документируемый результат, блокирующий лишь unify config. |

## Последовательность

```text
Wave 0: contract freeze + rollout design
                    ┌─ A: Event/catalog correctness ─┐
                    │                                  ▼
                    └─ B: typed bounded inject      C: two reader projections + SPA migration
                                                       │
                                     D: alignment procedure + Q7 ───────┘
```

| Волна | Работы | Условие завершения |
| --- | --- | --- |
| 0 | Утвердить перечисленные ниже predicates, token-cap unit и catalog migration/rollback. | Нет невыбранных semantics в A–D. |
| 1 | A и B параллельно. | Их unit/integration tests зелёные. |
| 2 | C после A. | Новая generation строит две корректные проекции; home мигрирован. |
| 3 | D после A; Q7 выполняется вручную. | Процедура и tests готовы; config меняется лишь после Q7=yes и отдельного подтверждения. |

## Work-package A — Event lifecycle и catalog correctness

### Контракт

1. `Event` сохраняется chat как и прежде.
2. Raw source cursor продвигается по количеству строк, прочитанных из LanceDB, либо predicate исключает `Event` до `Limit`/`Offset`. Поздний Fact/Decision не может быть потерян из-за Event в предыдущей странице.
3. `Event → Event` с неизменённой catalog eligibility не invalidates generation.
4. Любой переход, меняющий membership catalog, invalidates generation: non-Event → Event, Event → non-Event, delete catalog-eligible record, а также mutation catalog-eligible non-Event.
5. До первой V2 generation старые Ready generations считаются потенциально содержащими Event: rollout однократно и идемпотентно invalidates/rebuilds их для затронутого exact scope. Raw records и Event не удаляются.

### Реализация и границы

- Основные точки: `LanceDbMemoryStore.Persistence`, `LanceDbMemoryStore.ReaderCatalog`, catalog-generation persistence.
- Не изменять recall API, MCP Types, retention semantics или lifecycle модели.
- При откате вернуть предыдущий binary/format и rebuild catalog generation; не удалять raw records/wiki metadata.

### Acceptance

- Seed: больше одной raw page `Event`, затем Fact/Decision. Полная сборка содержит каждый допустимый record ровно один раз.
- `Event → Event` оставляет Ready generation stable.
- `Fact → Event` и `Event → Fact` каждый вызывают корректный rebuild/invalidate.
- Seed старой Ready generation c Event после migration перестраивается без Event в catalog/tree.
- Fact, Decision, Constraint, Outcome и Summary по-прежнему входят в eligible catalog при выполнении обычных условий scope/status/expiry.

## Work-package B — typed и едино ограниченный inject

### Контракт

1. Только chat ranked recall использует `Types = { Fact, Decision, Constraint, Outcome, Summary }`.
2. MCP `memory_recall` default Types и его внешний contract не меняются.
3. Ranked context ограничен максимум восемью hits и общим budget `800` токенов.
4. Если ranked result пуст, добавляется ровно один handoff: latest Summary, иначе latest Event. Handoff проходит тот же общий budget; он не добавляется к непустому ranked context.
5. `LastInjectHitCount` не используется как единственное доказательство: тестируется фактический text, передаваемый model gateway.

### Реализация и границы

- Основная точка: `LocalChatMemoryFeature` и существующий context builder/token accounting.
- Убрать текущую возможность передать fallback до 3200 символов вне общего budget либо заменить её тем же измеряемым token cap.
- Не вводить type-priority, новые контекстные веса или Event в ranked set.

### Acceptance

- Высокорелевантный Event не появляется в ranked context; каждый из пяти разрешённых типов может появиться.
- Ranked path содержит не более 8 hits и не превышает 800 токенов.
- Длинный Summary/Event fallback также не превышает 800 токенов; Summary предпочтительнее Event.
- При непустом ranked result handoff отсутствует.
- MCP stdio discovery/binding и `memory_recall` подтверждают неизменный default contract.

## Work-package C — две server-side reader projections и SPA home

### Контракт проекций

Обе проекции используют один immutable catalog generation и сначала применяют exact scope, Active status и expiry eligibility.

```text
Inbox        := non-Event AND wiki metadata отсутствует
CompiledTree := non-Event AND wiki metadata присутствует
```

- `Event`, в том числе ошибочно имеющий metadata, не входит ни в одну проекцию.
- Metadata — единственный признак compiled wiki; entities, название или client labels не заменяют его.
- Inbox и tree не содержат один record одновременно.

### Реализация и границы

- Основные точки: catalog builder, `BuildWikiLeavesAsync`, `MemoryReaderCatalogQueryService`, wiki tree read path и existing client API.
- Существующие `/api/memory-reader/catalog` и `/api/memory-reader/tree` используются без нового route. При необходимости уточняется filter/response contract существующего catalog endpoint — без второй generation.
- Home действительно мигрирует на `tree` API. Inbox остаётся отдельным UX path.
- Не допускается `Promise.all(tree + catalog|document)`; Ready path не выполняет full source scan.
- При rollout новая projection version/rebuild строит обе проекции из тех же raw records и metadata. Откат — rebuild предыдущей проекции, без удаления source data.

### Acceptance

- Event без metadata и Event с metadata отсутствуют в inbox и tree.
- Non-Event без metadata есть только в inbox.
- Non-Event с metadata есть только в tree; stale/missing metadata не превращается в wiki по fallback namespace.
- Pagination/facets/opaque continuation привязаны к выбранной projection и generation.
- SPA home вызывает tree API, не вызывает catalog, и client test запрещает параллельный tree+catalog/document fetch.
- Tree и document relations не ссылаются на исключённые records; Ready path не получает full source scan regression.

## Work-package D — Web ↔ MCP alignment и Q7

### Q7 evidence template

Проверка выполняется server/env-side. В репозиторий и browser payload не попадают исходные path/scope значения.

| Проверка | Web | MCP | Verdict |
| --- | --- | --- | --- |
| Нормализованный storage path | match / mismatch | match / mismatch | required |
| Tenant ID | match / mismatch | match / mismatch | required |
| Project ID (явный null допустим) | match / mismatch | match / mismatch | required |
| Workspace ID (явный null допустим) | match / mismatch | match / mismatch | required |
| Chat ID (явный null допустим) | match / mismatch | match / mismatch | required |
| Run ID (явный null допустим) | match / mismatch | match / mismatch | required |
| Actor ID / client ID | recorded separately | recorded separately | attribution/authorization only |
| Уникальный marker | Web observed | MCP stdio recall observed | supporting evidence |

Actor/client ID могут различаться: они не заменяют equality полного scope и не делают два store равными или разными сами по себе. `readerAligned` сохраняет существующее значение Reader ↔ Graph и является лишь Web-internal prerequisite. Равенство `ActiveMemoryCount` — только диагностика, не доказательство общности memory data.

### Выполнение

1. Добавить инструкцию и non-secret evidence template.
2. Автоматически проверить mismatch path и каждого scope field как не-aligned для процедуры; сохранить privacy browser DTO.
3. Выполнить MCP stdio initialize/tool discovery плюс status/recall с уникальным marker из тестируемого scope.
4. Q7=yes: предложить единый пример env/appsettings, но менять config только после отдельного явного подтверждения пользователя.
5. Q7=no: завершить D документом «два store», без тихой смены config. Это успешное закрытие D.

### Acceptance

- Procedure не принимает counts-only и не интерпретирует `readerAligned` как MCP proof.
- Совпадение full path + five scope fields и MCP observation формирует Q7 evidence; один mismatch фиксируется как mismatch.
- Browser не получает path, actor, scope, record text или marker.
- Нет новых MCP tools/routes и нет неявного config mutation.

## Общая матрица проверки

| Уровень | Что проверяется |
| --- | --- |
| Unit | A cursor/lifecycle; B Types, actual injected content и total cap; C predicates/facets/pagination; D path/scope comparison semantics и privacy DTO. |
| LanceDB integration | Event-heavy traversal, V1 Ready generation migration/rebuild, exact-scope isolation, wiki relation boundaries. |
| Web/client | Home tree request, inbox UX, no parallel fetch, `/memory-status` privacy и Reader↔Graph alignment semantics. |
| MCP integration | stdio initialize, tools discovery/binding, `memory_recall` unchanged, configured-scope marker recall. |
| Manual | Q7 evidence, config-unify decision, rollback/rebuild procedure. |

Перед merge каждого package: targeted tests, `git diff --check`, review текстов миграции/rollback. Полный suite выполняется перед выпуском V2.

## Итоговый DoD V2

План считается **implementation-ready**, если утверждены Wave 0 decisions и все следующие доказательства существуют:

1. A не пропускает records после Event, корректно обрабатывает lifecycle и мигрирует старые generations.
2. B не передаёт Event в ranked context и не обходит общий 8/800 budget fallback-строкой.
3. C серверно разделяет inbox и compiled tree, а home реально использует tree API.
4. D доказывает Web ↔ MCP по нормализованному path и полному scope либо честно фиксирует два store.
5. Не добавлены новые модели, Markov reranking, routes, MCP tools или ослабления privacy/exact scope.

## Основания

- [V1 gaps](01-current-baseline/gaps.md) — исходная цель и ограничения.
- [BCS RPA: точки сопряжения](05-bcs-rpa-connection.md) — контекст базы знаний и human-in-the-loop; не источник требований к моделям.
- `src/AgMemory.Storage.LanceDb/Catalog/LanceDbMemoryStore.ReaderCatalog.cs` — traversal, generation и wiki source.
- `src/AgMemory.Web/Features/Chat/LocalChatMemoryFeature.cs` — inject/handoff.
- `src/AgMemory.Contracts/Domain.cs`, `LocalMemoryReaderFeature.cs`, `LocalAgMemoryMcpRuntime.cs` — full exact scope и текущая Web/MCP граница.
