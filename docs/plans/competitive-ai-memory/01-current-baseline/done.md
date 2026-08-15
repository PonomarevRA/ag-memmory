# Baseline: уже сделано

Коммит ориентир: `0807400` — `feat(memory): speed up ready wiki and bound Yuki inject`  
(ветка `agent/memory-platform-bootstrap`).

## Wiki / Reader (Ready-path)

- Клиент: нет `Promise.all(tree + catalog|document)`; catalog или document по запросу.
- UI: children / related / backlinks на document view.
- LanceDB: Ready tree + relations читают **compiled generation**, без per-row `ReadCurrent*` / полного facet-scan Records.
- Catalog facets на Ready path выровнены с compiled.

## Chat inject (Yuki)

- SearchLimit **8**, TokenBudget **800**.
- При empty recall — handoff-line: latest **Summary**, иначе latest **Event** (вне ranked Types).
- `LastInjectHitCount` трекается для диагностики.

## Memory status / diagnostics

- UI/API: `lastInjectHitCount`, `readerAligned` (сравнение Reader vs Graph: path + actor + scope).
- Budget 800 как UI constant.
- Браузер по-прежнему без текстов memory, scope, actor, entity names, persistent IDs.

## MCP surface (не расширяли)

Три инструмента: `memory_recall`, `memory_remember`, `memory_status`.  
Path/actor/scope из env процесса, не из tool args.

## Что это даёт относительно ai-memory

| Идея | Статус в baseline |
| --- | --- |
| Bounded handoff | Частично (chat empty-recall) |
| Token budget | Да (8+800) |
| Compiled wiki reads | Да на Ready-path |
| Authority-aware Types | **Нет** (Types=null → волна B) |
| Event ∉ wiki | **Нет** (волны A, C) |
| Web↔MCP alignment | Диагностика есть; процедура — волна D |
