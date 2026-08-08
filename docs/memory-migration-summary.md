# Сводка реализации Memory Platform

> Обновляется после завершения каждой фазы. Подробности и внешние блокеры — в [реестре проблем](memory-migration-problems.md).

| Фаза | Статус | Готовый результат | Что остаётся |
| --- | --- | --- | --- |
| 1. AGM analysis | Локально завершена | Документировано фактическое AGM поведение и добавлен безопасный versioned synthetic corpus. | Production snapshot/real query baseline только после data approval. |
| 2. Domain | Локально завершена | Contracts/Core с typed commands, exact scope, redaction/idempotency, RRF и budgeted context; 16 новых tests. | Provider port firewall остаётся отдельной Phase 3 задачей. |
| 3. Storage abstraction | Локально завершена | Scope-safe ports, batch primitive и firewall tests; 74 solution tests проходят. | Adapter должен реализовать эти ports без provider leakage. |
| 4. LanceDB | Локально завершён на macOS | Adapter реализует durable writes/reopen, batch/filter/vector+lexical paths; 79 solution tests проходят. | Linux/FTS/concurrency/index/schema gates. |
| 5. Initial schema | Локально завершена | Versioned fail-closed schema manifest, fingerprint и embedding contract validation; adapter suite 9/9. | Schema evolution beyond compatible table registration. |
| 6. Hybrid retrieval | Локально завершена | Provider-neutral parallel fusion, deterministic dedupe, hot-first cited context and real LanceDB integration; 96 solution tests pass. | Production SLO and explicit rendered-token measurement. |
| 7. Hot memory | Локально завершена | Structured session state, bounded TTL, deterministic decay and promotion eligibility; 105 solution tests pass. | Dedicated durable decision trace belongs to Phase 8. |
| 8. Decision memory | Локально завершена | Compact decision trace uses central redaction/idempotency/transaction pipeline; 110 solution tests pass. | Client/MCP exposure belongs to Phase 10. |
| 5–12 | Запланированы | Задачи распределены на [доске](memory/task-board.md). | Реализация после domain/adapter boundary. |

## Человеческий итог на текущий момент

Работа идёт в отдельном `ag-memmory` workspace и не меняет AGM. Compatibility с legacy AGM больше не является обязательной: новая платформа будет подключаться как модуль после controlled rollout, а existing AGM data можно удалить решением владельца. Реальные rollout, import и delete всё ещё требуют безопасных preconditions; локальная реализация не объявляет их выполненными заранее.
