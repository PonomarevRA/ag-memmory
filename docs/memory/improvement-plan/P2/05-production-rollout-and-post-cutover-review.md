# P2.5. Поэтапный rollout и review после cutover

**Владелец:** release owner + security reviewer
**Зависимости:** P2.4
**Статус:** conditional

## Цель

Расширять новую модель контролируемо и оставить проверяемое решение о завершении.

## Сделать

- Составить волны включения, monitoring, rollback thresholds и ответственных.
- На каждой волне проверять isolation, redaction, latency, audit integrity и reader/MCP regression.
- Провести post-cutover review с решением продолжить, остановить или откатить rollout.

## Приёмка

По каждой волне есть evidence; итоговое решение и состояние flags задокументированы.
