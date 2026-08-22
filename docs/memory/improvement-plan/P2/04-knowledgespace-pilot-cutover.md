# P2.4. Пилотный cutover области знаний

**Владелец:** QA + release owner
**Зависимости:** P2.3
**Статус:** conditional

## Цель

Проверить новую модель на одной тестовой области до расширения.

## Сделать

- Выбрать изолированный pilot dataset и включить feature flags только для него.
- Провести сценарии MCP, chat, reader, tree, tags, records browser и usage dashboard.
- Проверить backup/restore, monitoring и критерии немедленного rollback.

## Приёмка

Есть доказательство isolation и восстановления; владелец релиза явно одобрил либо откатил пилот.
