# Матрица сравнения: AgMemory vs ai-memory

## Позиционирование

| | AgMemory (ag-memmory) | ai-memory |
| --- | --- | --- |
| Ядро | .NET Contracts/Core + LanceDB | Rust server + markdown wiki в git |
| Модель знаний | Typed records (`Fact`, `Decision`, `Event`…) + wiki generation | Pages, compiled из lifecycle observations |
| Агенты | MCP: `memory_recall` / `remember` / `status`; Web Yuki chat | Hooks + MCP write/query/handoff на многих CLI |
| UI | Vite SPA: reader, status, graph | Built-in `/web` tree + FTS |
| Изоляция | Exact scope (tenant/project/…) | workspace_id / project_id + `_global` |
| Экономия токенов | SearchLimit 8, TokenBudget 800, strip ID | Bounded handoff + authority-aware truncation |

## Сильные стороны AgMemory

- Строгие contracts и exact-scope; browser не получает ID/scope/тексты status.
- Локальный loopback Development: chat + graph + status на одном host options.
- Hybrid retrieval / LanceDB уже в платформе (не нужно babysit git wiki).
- После `0807400`: lazy wiki, relations UI, Ready-path без full source scan, handoff-line.

## Сильные стороны ai-memory (идеи)

- **Compiled wiki** из наблюдений, не сырой лог сессии.
- **Bounded handoff** «where you left off» между агентами.
- **Authority-aware recall**: rules/decisions/procedures > episodic.
- **Entity-assisted recall** без LLM на query-time.
- **Capture exclusions** и per-project isolation by construction.
- Широкая матрица клиентов (не копировать hooks — копировать *continuity contract*).

## Слабые места AgMemory относительно цели «память экономит токены»

1. Chat `Remember` каждый turn как `Event` → catalog invalidate/rebuild cost (волна A).
2. Chat inject `Types = null` → Event конкурирует с Fact/Decision (волна B).
3. Inbox/tree могут смешивать эпизоды с wiki (волна C).
4. Web host и MCP process могут указывать на разный path/scope (волна D).

## Что не сравнивать 1:1

- Замена LanceDB на git-md.
- Перенос lifecycle hooks на Codex/Cursor/Claude «как у них».
- Их MCP surface (`memory_write_page`, `handoff_accept`) — Out этого плана.
