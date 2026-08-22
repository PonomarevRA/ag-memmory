# P2.1. Контракт `KnowledgeSpaceId` и авторизация

**Владелец:** Contracts + Core
**Зависимости:** положительный ADR P0.1
**Статус:** conditional

## Цель

Добавить новую scope dimension только если current five-field mapping доказанно недостаточен.

## Сделать

- Определить `KnowledgeSpaceId`, его источник, cardinality, exact authorization и server-side configuration.
- Запретить клиенту/MCP tool задавать произвольное значение; валидировать его до доступа к store.
- Версионировать контракты и определить поведение legacy records.

## Приёмка

Cross-space injection отклоняется, а контракт не меняет данных до миграции P2.3.
