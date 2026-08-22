# P0.2. Политика раскрытия записей и аудита

**Владелец:** security reviewer + Web/Core
**Зависимости:** нет
**Статус:** completed (RDP-001; реализация остаётся в P1)

## Цель

Утвердить testable field policy до record browser и журнала.

## Сделать

- Составить allowlist browser DTO: safe row key, preview и разрешённые метаданные.
- Явно запретить durable IDs, raw scope, actor, storage path, embeddings и evidence.
- Решить, когда допустим исходный текст; provenance ограничить allowlisted client label.
- Зафиксировать Development+loopback доступ и `no-store`.

## Приёмка

Есть матрица «поле → разрешено/запрещено → причина → тест», применимая к API и UI.

## Результат

[RDP-001. Раскрытие записей и аудита](record-and-audit-disclosure-policy-v1.md) фиксирует allow/conditional/deny matrix, требования к DTO и негативные API/UI security-тесты. Она не разрешает raw text или raw query без отдельных gates P0.3 и P0.5.
