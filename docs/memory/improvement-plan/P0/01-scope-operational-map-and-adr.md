# P0.1. Карта scope и ADR

**Владелец:** архитектор + Core-разработчик
**Зависимости:** нет
**Статус:** completed (ADR-001; production mapping требует отдельного approval)

## Цель

Доказать, достаточны ли tenant/project/workspace/chat/run для предметных областей памяти.

## Сделать

- Составить mapping областей на пять exact-scope полей, владельца словаря и допустимую cardinality.
- Зафиксировать matrix «процесс MCP/Web → store → scope» для общих и изолированных областей.
- Принять ADR: использовать текущий scope либо открыть conditional-ветку `KnowledgeSpaceId`.

## Приёмка

ADR содержит решение, обоснование и список процессов с разной конфигурацией. Никаких schema/API изменений в этой задаче нет.

## Результат

[ADR-001. Текущий exact scope как граница памяти](ADR-001-current-exact-scope.md) сохраняет five-field контракт для P1, фиксирует обязательное approval для deployment mapping и определяет evidence-триггер для conditional-ветки `KnowledgeSpaceId`.
