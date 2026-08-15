# Adopt / Reject / Defer

Референс: [akitaonrails/ai-memory](https://github.com/akitaonrails/ai-memory).  
Правило: **идеи да, чужой стек нет.**

## Adopt (в AgMemory-терминах)

| Идея ai-memory | Как переносим | Волна |
| --- | --- | --- |
| Compiled wiki / не сырой лог | Event ∉ catalog/wiki eligibility; Persist(Event) не инвалидирует generation | A, C |
| Authority-aware recall | Chat inject Types ≠ Event; Fact/Decision/Constraint/Outcome/Summary | B |
| Bounded handoff | Уже: Summary else Event при empty recall; дальше не раздувать MCP | done + B |
| Entity-assisted recall | Уже: SharedEntity в graph; дальше — без новых MCP tools в этом плане | defer за D |
| Cross-agent continuity | Один path+actor+tenant Web↔MCP; provenance `AGMEMORY_CLIENT_ID` | D |
| Capture exclusions | Не писать шум в wiki/catalog (Event исключить из wiki) | A, C |
| Token-saving inject | 8 hits + 800 tokens зафиксировать; не расширять бюджет «на всякий» | B |

## Reject (явно не делаем)

| Идея / практика | Почему |
| --- | --- |
| Git + markdown как primary store | AgMemory = LanceDB + Contracts; dual-write усложнит scope |
| Lifecycle hooks на все CLI | Не наш продукт; MCP + Web достаточно для локального bridge |
| MCP `memory_write_page` / `handoff_accept` | Ломает 3-tool surface; handoff уже внутри chat inject |
| Shared-server / production auth как у них | Loopback Development only; отдельный трек |
| Замена LanceDB / vector-first как DoD | Hybrid уже есть; DoD = Ready-path + inject discipline |
| Type-priority ranking | Critic: Out; достаточно Types filter + budget |
| `chatAligned` diagnostic | Critic: Out; достаточно `readerAligned` + K |

## Defer (после A–D или вне пакета)

- Semantic embeddings / RRF как отдельный quality-трек.
- Entity recall как ranked inject signal (не только graph edges).
- Invalidate-on-Event уже закрывается волной A; полный rebuild policy review — после A.
- Production multi-writer / auth.

## Канон Event (зафиксировать во всех WP)

- **Event** = chat episode / transcript atom.
- Event **не** wiki page, **не** catalog leaf, **не** ranked inject candidate.
- Handoff Event-line = last resort **вне** Types ranked set.
- `K=1` (один inject hit) **не** делает Event eligible для Types.
