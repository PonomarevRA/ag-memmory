# Доска задач Memory Platform

> Владелец: migration orchestrator. Каждая строка — отдельная задача из активного плана, а не обещание external rollout.

| Фаза | Статус | Владелец | Артефакт / следующий результат |
| --- | --- | --- | --- |
| 1. AGM analysis | done (local) | phase0_baseline | `docs/memory/current-state.md`; production corpus требует data approval. |
| 2. Domain model | done (local) | phase2_contracts_core | Contracts/Core и 16 новых tests; build 0 warnings/errors. |
| 3. Storage abstraction | done (local) | phase3_storage_abstraction | Scope-safe ports, dependency firewall и 74 passing solution tests. |
| 4. LanceDB adapter | active | phase4_lancedb_adapter | Production adapter + integration checks. |
| 5. Initial schema | planned | schema task | Versioned LanceDB schema/mapping. |
| 6. Hybrid retrieval | planned | retrieval task | Query executor, RRF, bounded cited context. |
| 7. Hot memory | planned | hot-memory task | TTL/decay/promotion use cases. |
| 8. Decision memory | planned | decision-memory task | Compact trace commands and validation. |
| 9. Existing migration | planned | migration task | Import CLI, manifest, replay/parity. |
| 10. Agent integration | planned | integration task | Client plus four MCP tools. |
| 11. Token economy | planned | token-policy task | Enforcement/reporting tests. |
| 12. Validation | planned | benchmark task | Query-backed benchmark reports. |

## Правила handoff

Каждый владелец добавляет `docs/phase-<n>-summary.md` и `docs/phase-<n>-open-problems.md`, отмечает только свою строку/чеклист после проверки и передаёт точные files/tests следующему владельцу. Новые risk/blocker также добавляются в общий [реестр](../memory-migration-problems.md).
