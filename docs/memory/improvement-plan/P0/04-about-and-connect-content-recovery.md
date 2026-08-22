# P0.4. Восстановление «О проекте» и «Подключение агента»

**Владелец:** Web-разработчик + technical writer
**Зависимости:** нет
**Статус:** completed

## Цель

Вернуть фактический текст из README в UI без раскрытия локальной конфигурации.

## Сделать

- Подготовить About: назначение, локальное хранение, границы безопасности и ограничения.
- Подготовить Connect: Codex/Cursor/Claude, три MCP tools, placeholders для env и правило общей памяти.
- Дать ручную проверку `memory_status → memory_remember → memory_recall`; не обещать browser-вызовы MCP.

## Приёмка

Тексты совпадают с README, содержат только placeholders и не зависят от решения о `KnowledgeSpaceId`.

## Результат

Страницы [About и Connect](../../../../src/AgMemory.Web/client/src/pages/index.ts) восстановлены в SPA: они описывают локальные границы, Codex/Cursor/Claude, три MCP tools и ручную проверку. В DOM остаются только `YOUR_*` placeholders; реальная конфигурация не показывается.
