# P1.10. Nonbreaking integration и security regression

**Владелец:** QA + security reviewer
**Зависимости:** P1.2–P1.9
**Статус:** planned

## Цель

Подтвердить, что новые возможности не ломают reader, MCP, graph, chat и status.

## Сделать

- Провести end-to-end матрицу feature flags, exact-scope isolation, reader/MCP/alignment и закрытых маршрутов.
- Проверить no-store, redaction, cursor replay/cross-area denial и audit outage.
- Сверить About/Connect с README и существующие regression suites.

## Приёмка

Целевой набор .NET/Vite/MCP тестов проходит; security checklist приложен к релизному решению.
