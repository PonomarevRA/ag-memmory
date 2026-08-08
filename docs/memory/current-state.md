# Текущее состояние AGM memory

Актуальное evidence-based описание находится в [current-state-agm.md](../current-state-agm.md). Оно фиксирует entities, SQLite storage model, retrieval/prompt flow, ограничения и risks без изменения AGM.

## Короткий вывод

AGM прямо управляет собственными SQLite tables `workspace_memory*` и `fanout_memory*`; `WorkspaceMemoryLifecycleObserver` — главный direct-SQL coupling point. Workspace summary ограничена 2 000 символами, fan-out memory имеет run scope и TTL. Scope formats несовместимы между workspace, canonical и retrieval representations, поэтому новая платформа применяет единый exact scope model до любого поиска.

Версионированный synthetic corpus расположен в `docs/quality-corpus/agm-memory-stage-zero-v1.json`. Он полезен для contract/regression checks, но не заменяет approved real-data quality baseline.
