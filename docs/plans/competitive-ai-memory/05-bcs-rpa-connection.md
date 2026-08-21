# БКС RPA: точки сопряжения с AgMemory и границы фактов

**Статус:** исследовательская записка, не план реализации.

**Источник:** [«40+ роботов, 4 человека: правила для маленькой команды»](https://habr.com/ru/companies/bcs_company/articles/1067292/) (БКС Финтех, проверено 2026-08-20).

**Цель:** отделить подтверждённые сведения статьи от фактического состояния `AgMemory.*` и от возможных будущих работ.

## Короткий вывод

Статья описывает эксплуатационную модель RPA: небольшая команда сопровождает парк роботов, ведёт базу знаний в Confluence, контролирует запуски и передаёт человеку финальное исполнение финансовых операций. Это хорошо сопрягается с AgMemory как с локальной, scope-bound памятью для инструкций, итогов проверок и handoff.

Статья **не раскрывает** названия или версии моделей векторизации и распознавания, формулы весов, а также цепи Маркова. Отсутствие таких сведений в публикации не доказывает, что они не используются внутри БКС; оно означает только, что их нельзя приписывать статье или брать как техническое требование.

## Что подтверждает статья

| Факт | Значение для AgMemory | Статус |
| --- | --- | --- |
| Более 40 роботов обслуживаются командой из 4 человек; упомянута экономия около 30 тыс. человеко-часов в год. | Память должна быть компактной, воспроизводимой и не требовать постоянного ручного сопровождения. | Подтверждено статьёй |
| База знаний в Confluence включает шаблоны ТЗ, описания действующих и выведенных роботов, инструкции, шаблоны писем и задач, скрипты и распределение обязанностей. | Источник знаний можно заносить в AgMemory как типизированные, scope-bound записи с provenance, а не как неограниченный чат-лог. | Подтверждено статьёй; способ интеграции — вывод анализа |
| Роботы контролируются по логам и отчётам; есть служебные скрипты для контроля запусков и сводной статистики. | Подходят `Outcome`, `Incident`, `Procedure` и `Summary`, а не `Event` в качестве wiki-страницы. | Статья + вывод анализа |
| Роботы не принимают финальные финансовые решения и не исполняют транзакции; предусмотрен ручной сценарий на сбой. | Память может давать контекст и чек-лист, но не должна быть источником автономного финансового действия. | Подтверждено статьёй |

## Модели векторизации и распознавания

### Что известно из статьи

| Область | Модель / версия | Статус |
| --- | --- | --- |
| Векторизация / embeddings | Не раскрыты | В статье отсутствуют |
| Распознавание речи (ASR) | Не раскрыта | В статье отсутствует упоминание ASR или аудиовхода |
| OCR / распознавание изображений | Не раскрыты | Статья говорит только об ИИ для неструктурированных документов; тип модели не указан |
| LLM / агент | Не раскрыты | Упомянута будущая коллаборация с ИИ, без поставщика, модели и контура данных |

Нельзя подставлять Whisper, конкретную embedding-модель или OCR-модель вместо этих значений: это было бы предположением, а не результатом исследования.

### Фактическое состояние AgMemory

| Контур | Provider / model / version | Размерность / нормализация | Вывод |
| --- | --- | --- | --- |
| Local Web chat | `local-chat` / `lexical-fallback` / `v1` | `1` / `none`, вектор `[1f]` | Техническая заглушка, не semantic embedding. |
| Local MCP | `codex-mcp` / `lexical-fallback` / `v1` | `1` / `none`, вектор `[1f]` | Техническая заглушка, не semantic embedding. |
| Распознавание речи, OCR | Нет pipeline, порта или модели в актуальном `AgMemory.*` runtime | — | Не реализовано и не подтверждено. |

При этом Contracts уже допускают смену реализации без смены архитектуры: `IEmbeddingProvider` создаёт вектор, `IEmbeddingPolicy` выдаёт версионированный `EmbeddingContract`, а LanceDB разделяет vector tables по provider/model/version/dimension/normalization. Реальная модель может быть подключена позднее за этими портами, но это отдельное решение по данным, privacy, retention, удалению и качеству; оно не входит в A–D.

## Система весов: что реально считается

### 1. Активный runtime: hybrid retrieval

Core сначала отсекает записи вне exact scope, неактивные, истёкшие и не подходящие по `Types`, а затем объединяет lexical и vector кандидатов reciprocal rank fusion (RRF):

```text
lexicalContribution = 1 / (K + lexicalRank)
vectorContribution  = 1 / (K + vectorRank)
fusedScore          = lexicalContribution + vectorContribution
finalScore          = fusedScore + graphScore
```

`K` по умолчанию равен `60`. Это не обученные веса и не коэффициенты важности типов. `Importance` и `Confidence` хранятся в record, но в этой формуле RRF не участвуют. Graph score является опциональным: Local Web и MCP создают `MemoryQueryService` с `IMemoryGraph = null`, поэтому в их текущем runtime graph contribution отсутствует.

Отдельно, в текущем graph browser ребро `Weight` — это число общих нормализованных entities у пары memories. Такой вес описывает структурную близость, но не вероятность перехода и не марковскую матрицу.

### 2. Legacy-compatible пакет: дополнительные веса контекстного и graph reranking

`src/Compatibility/Agm.Memory.*` — временная legacy-compatible граница, а не активная композиция standalone Web/MCP. В ней имеются два независимых механизма:

1. Контекстный reranker, выключенный по умолчанию, считает линейную комбинацию `relevance`, `rrf`, `graph`, `confidence` и актуальности. Его defaults: `0.30`, `0.10`, `0.10`, `0.10`, `0.40` соответственно.
2. `MemoryGraphReranker`, также выключенный по умолчанию, задаёт веса типов ребра: same entity `1.00`, same topic `0.65`, same session `0.80`, references `0.90`, derived from `1.00`, same project `0.15`.

Эти значения нельзя считать политикой текущего `AgMemory.*` runtime и не следует переносить в план A–D автоматически.

## Цепи Маркова: где они есть и где их нет

В статье БКС цепи Маркова не упомянуты. В актуальном standalone `AgMemory.*` они не выполняются: Web и MCP не передают graph port в `MemoryQueryService`.

Однако в legacy `MemoryGraphReranker` реализован ограниченный random walk with restart — марковский обход графа кандидатов:

```text
next(v) = restartProbability * personalization(v)
        + Σ[u→v] (1 - restartProbability) * current(u) * weight(u,v) / Σ[x] weight(u,x)
```

- стартовое распределение `personalization` строится из exact lexical/vector seeds; если их нет — из кандидатов с положительным score;
- по умолчанию: не более 2 шагов, restart probability `0.35`, максимум 10 соседей;
- вес перехода равен `edge.Strength × configuredEdgeWeight × temporalDecay`;
- `temporalDecay = 0.5^(age / 30 days)` по умолчанию;
- средняя накопленная масса умножается на `lambda = 0.20` и добавляется к исходному score; exact seeds закреплены перед результатами расширения;
- необязательный session boost ограничен `0.10`.

Это алгоритм расширения и reranking кандидатов, а не модель жизненного цикла памяти: он не меняет authority типов, не создаёт facts и не отменяет exact-scope authorization.

## Точки сопряжения со статьёй и gaps A–D

| Практика из статьи | Точка в проекте | Минимальное применение без смены глобальной архитектуры | Связь с gaps |
| --- | --- | --- | --- |
| Живая база знаний и архив выведенных роботов | Typed records, lifecycle, compiled wiki | Сохранять проверенные инструкции как `Procedure`/`Fact`, вывод эксплуатации как `Outcome`/`Incident`; использовать lifecycle вместо удаления истории. | A, C |
| Логи запусков и ежедневные сводки | MCP/Web memory + `Summary`/`Outcome` | Передавать в память короткий итог, ссылку/идентификатор первичного лога и provenance, но не сырой поток сообщений. | A, B |
| Человек принимает финальное решение | Exact scope, browser privacy, MCP fixed environment | Возвращать оператору контекст, чек-лист и найденные расхождения; не выдавать автономную команду на платёж или изменение учётных систем. | D |
| Гибрид «ИИ — мозг, робот — надёжные руки» | Provider-neutral contracts и existing RPA/API integration boundary | ИИ может извлечь кандидаты из документа; детерминированный робот сверяет и заполняет черновик; человек подтверждает. Модель извлечения не добавляется в Core. | Вне A–D; отдельный proposal |
| Кросс-клиентская непрерывность | Web/MCP shared storage and exact scope | Сверять нормализованный путь и **все** поля scope; actor и client ID использовать для attribution, не как замену scope. | D |

## Необходимые условия перед будущим пилотом распознавания или semantic embeddings

1. Утвердить класс данных, согласие/правовое основание, retention и удаление исходного аудио/документа и транскрипта.
2. Выбрать модель и зафиксировать provider, model, version, dimension, normalization в `EmbeddingContract`; для ASR — язык, лимиты длины, качество и способ хранения исходника.
3. Сохранить exact-scope eligibility до ranking и не раскрывать текст, scope, actor, entity names или persistent IDs в browser status/graph payload.
4. Измерять качество отдельно: recall@K, nDCG@K, MRR, стоимость и задержка; не подменять этим acceptance A–D.
5. Для graph walk отдельно утвердить источник рёбер, направленность, decay, ограничения соседей и offline evaluation. Не включать его в standalone runtime без отдельного contract/design review.

## Проверка утверждений в репозитории

```bash
rg -n 'lexical-fallback|new float\\[\\] \{ 1f \}|IEmbeddingProvider|IEmbeddingPolicy' src
rg -n 'ReciprocalRankConstant|lexicalContribution|vectorContribution|GraphContribution' src/AgMemory.Core
rg -n 'RestartProbability|WalkSteps|MemoryGraphEdgeWeights|MemoryGraphReranker' src/Compatibility
rg -n 'speech|recognition|whisper|audio|transcrib' src --glob '!**/wwwroot/vendor/**'
```

## Источники

- [Статья БКС Финтех](https://habr.com/ru/companies/bcs_company/articles/1067292/), доступ и проверка: 2026-08-20.
- `docs/plans/competitive-ai-memory/01-current-baseline/gaps.md` — ограничения текущего плана: Vector/RRF и type-priority ranking не являются DoD A–D.
- `src/AgMemory.Core/Retrieval/DeterministicRetrieval.cs` и `MemoryCoreOptions.cs` — active RRF.
- `src/AgMemory.Core/Graph/MemoryGraphQueryService.cs` — weight графа browser.
- `src/AgMemory.Web/Features/Chat/LocalChatMemoryFeature.cs` и `src/AgMemory.McpServer/LocalAgMemoryMcpRuntime.cs` — фактические local embedding stubs и отсутствие graph port.
- `src/Compatibility/Agm.Memory/MemoryGraphReranker.cs` и `src/Compatibility/Agm.Memory.Abstractions/MemoryGraph.cs` — legacy random walk with restart.
