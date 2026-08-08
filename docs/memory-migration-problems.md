# Реестр проблем и решений для доработки миграции памяти

> Владелец: migration orchestrator  
> Обновлено: 2026-08-08  
> Правило: здесь остаются только реальные внешние блокеры, риски и принятые технические уточнения. Исходный код и план остаются источниками правды о текущем поведении и статусе работ.

## Как читать реестр

- **Блокер** требует решения или действия за пределами текущей реализации; соответствующая фаза не отмечается завершённой раньше времени.
- **Открыто** — известный риск с назначенным следующим действием.
- **Решено технически** — уточнение уже можно реализовывать; оно не заменяет продуктового решения.

## Активные вопросы

| ID | Статус | Проблема | Влияние | Следующее действие / владелец | Источник |
| --- | --- | --- | --- | --- | --- |
| P0-01 | Блокер | Не утверждены преобразование legacy workspace/chat/run в tenant/project и источник авторизации. | Нельзя безопасно включить реальную миграцию или tenant canary. | Product + security owner утверждают scope map и правила selector. | [phase-0-open-problems.md](phase-0-open-problems.md) |
| P0-02 | Блокер | Не утверждены retention, deletion, tombstone/purge и legal hold. | Нельзя выбирать, какие legacy events/candidates удалять или переносить. | Privacy/data owner фиксирует policy. | [phase-0-open-problems.md](phase-0-open-problems.md) |
| P0-03 | Блокер | Не утверждён embedding provider/model/dimension/version/re-embedding policy. | Векторный production index и показатели качества пока не могут быть приняты. | ML/platform owner фиксирует embedding contract. | [phase-0-open-problems.md](phase-0-open-problems.md) |
| P0-04 / P1-01 | Открыто | Не выбран private NuGet feed, publisher identity и rollback policy. | Локальные пакеты проверены, но release и package-only consumption в AGM не доказаны. | Release/platform owner выбирает registry и публикует версию. | [phase-1-open-problems.md](phase-1-open-problems.md) |
| P0-05 | Блокер | Нет решения о самостоятельности или интеграции `mcp-ai-memory`. | Нельзя считать MCP integration архитектурно закрытой. | Product/architecture owner принимает решение. | [phase-0-open-problems.md](phase-0-open-problems.md) |
| P1-02 | Открыто | AGM ещё использует source `ProjectReference`, а не released package. | Cross-repository exit criterion Phase 1 не проверен. | После P1-01 владелец AGM переключает references и прикладывает evidence. | [phase-1-open-problems.md](phase-1-open-problems.md) |
| P3-01 | Открыто | Spike подтверждён только на macOS arm64. | Linux x64 support нельзя объявить готовой до runtime suite на Linux. | Storage owner запускает тот же integration suite на supported Linux x64 runner. | [phase-3-open-problems.md](phase-3-open-problems.md) |
| P3-02 | Открыто | LanceDB 2.5.0 binding проверен для core CRUD/vector filter, но не для FTS, RRF, index rebuild, schema evolution или concurrent access. | Production adapter exit criteria ещё не доказаны. | Adapter owner добавляет integration tests, либо фиксирует supported alternative за ports. | [phase-3-open-problems.md](phase-3-open-problems.md) |
| P3-03 | Открыто | NuGet binding опубликован `lennylxx/LanceDB`, хотя использует official Lance Rust crate. | Требуется supply-chain/license/provenance review до production rollout. | Platform/security owner утверждает provenance/version pin. | [phase-3-open-problems.md](phase-3-open-problems.md) |
| P-GH-01 | Открыто | На текущей машине отсутствует GitHub CLI `gh`. | Автоматический push + draft PR через установленный GitHub workflow недоступны. | Установить `gh`, выполнить `gh auth login`, затем publish branch. | Local check 2026-08-08 |

## Технические уточнения, уже принятые для реализации

| ID | Статус | Решение | Причина и границы | Источник |
| --- | --- | --- | --- | --- |
| TD-P2-01 | Решено технически | Canonical `MemoryRecord` содержит nullable redacted `Reason` (или эквивалентное переносимое detail-поле). | Legacy `workspace_memory.reason` обязан сохраняться при Phase 4; поле проходит тот же ingress redaction и не становится источником неограниченного raw content. | [phase-2-contract-core-brief.md](phase-2-contract-core-brief.md), [migration-from-agm.md](migration-from-agm.md) |

## Фиксировать при следующих фазах

- Phase 3: фактическая совместимость .NET LanceDB binding с macOS и Linux, включая concurrent access и schema evolution.
- Phase 4: строки snapshot, которые не импортированы или не прошли parity, с replay instructions.
- Phase 5: любые quality/latency regression относительно одобренного baseline и метрики, недоступные до P0 решений.
- Phase 6–8: результаты canary/rollback, command/outbox delivery, MCP parity и сроки архивирования legacy SQLite.
