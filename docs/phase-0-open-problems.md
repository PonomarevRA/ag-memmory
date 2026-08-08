# Фаза 0 — открытые вопросы и проблемы для доработки

> Владелец файла: `phase0_baseline`  
> Статус: требуется явное решение; это не утверждённые архитектурные решения.

## P0 — обязательные для закрытия Phase 0

| ID | Проблема | Почему блокирует | Нужное решение / владелец |
| --- | --- | --- | --- |
| P0-01 | Нет утверждённого отображения `tenant/project/workspace/chat/run`. Текущий workspace service scope — GUID workspace, canonical scope — GUID tenant + optional project, retrieval scope — строки tenant/project. | Нельзя доказать scope isolation или сформировать единую `MemoryScope`. | Product + security owner утверждают cardinality, обязательность каждого поля и authorization source. |
| P0-02 | Retention/deletion не утверждены. Workspace summary и candidates не имеют TTL; `ForgetAsync` удаляет только summary; fan-out expiry lazy. | Нельзя безопасно определить export/import eligibility и выполнить deletion parity. | Privacy/data owner задаёт сроки, purge mechanism, legal hold и каскады delete. |
| P0-03 | Embedding provider, model, dimension, normalization и version policy не выбраны. | Нельзя зафиксировать LanceDB schema, reproducibility или качество vector search. | Platform/ML owner фиксирует provider/model/version/re-embedding policy. |
| P0-04 | Нет подтверждённого private NuGet feed, registry access model и compatibility/pinning policy. | Phase 1 не может доказать независимую публикацию и потребление пакетов AGM. | Release/platform owner выбирает feed, package identities, version/rollback policy. |
| P0-05 | Судьба соседнего `mcp-ai-memory` не утверждена. | Нельзя корректно задать MCP boundary: он использует другую PostgreSQL/pgvector архитектуру. | Product + architecture owner выбирают `separate` или документированный integration path. |

## P1 — baseline gaps, которые нельзя выдавать за измерения

| ID | Наблюдение | Следующее безопасное действие |
| --- | --- | --- |
| P1-01 | В исходнике есть только synthetic six-query corpus. Текущий evaluator получает ranked hits снаружи и не выполняет реальный retrieval. | После P0-01/P0-02 создать approved anonymized exported corpus и сохранять execution reports отдельно. |
| P1-02 | Нет предоставленного anonymized SQLite snapshot. | Создать export API/CLI и manifest в Phase 4; не копировать SQLite database из production в этот репозиторий. |
| P1-03 | Redaction распределён между extractor, SQLite writer и fan-out context; regex coverage не является центральной ingress guarantee. | В Phase 2 сделать единый ingress redaction port до persistence и добавить negative fixtures. |
| P1-04 | Expired fan-out rows удаляются только при read/append; candidates имеют статусы, но не documented physical purge. | Выбрать janitor/purge policy и включить её в migration eligibility и deletion tests. |
| P1-05 | `WorkspaceMemoryLifecycleObserver` напрямую читает AGM runtime tables и управляет module-owned tables. | Phase 7 заменить это normalized command/outbox boundary; не переносить direct SQL в новую платформу. |

## Принятые ограничения этой фазы

- Не использовались AGM/AGM MCP tools.
- Не изменялись production-код, исходный AGM-репозиторий и данные.
- Не создавался глобальный issue tracker: этот файл ограничен проблемами Phase 0.
