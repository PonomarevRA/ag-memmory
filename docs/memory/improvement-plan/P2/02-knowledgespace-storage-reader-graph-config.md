# P2.2. `KnowledgeSpaceId` в storage, reader, graph и конфигурации

**Владелец:** Storage + Core + Web
**Зависимости:** P2.1
**Статус:** conditional

## Цель

Применить новую dimension ко всем ключам и read-проекциям без смешения legacy данных.

## Сделать

- Расширить storage keys, selectors, compiled catalog, wiki relations, tree/tags/search и graph queries.
- Обновить конфигурацию Web/MCP и feature-flagged reader selection.
- Сохранить явное разделение legacy и migrated generations.

## Приёмка

Тесты подтверждают isolation между spaces, отсутствие дубликатов и неизменность legacy-пути до cutover.
