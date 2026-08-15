# Baseline: gaps к цели «память экономит токены + понятная wiki»

## Gap A — Event ломает / шумит catalog generation

**Симптом:** каждый chat turn делает `Remember(Event)` → риск invalidate catalog generation и дорогого rebuild.  
**Против ai-memory:** у них observations компилятся в wiki; сырой эпизод не становится «страницей» по умолчанию.  
**Нужно:** Persist(Event) **не** инвалидирует catalog generation; Event ∉ catalog eligibility. Chat по-прежнему пишет Event.

## Gap B — inject без authority filter

**Симптом:** chat Recall `Types = null` → Event конкурирует с Fact/Decision за 8 слотов и 800 токенов.  
**Против ai-memory:** authority-aware recall (rules/decisions > episodic).  
**Нужно:** Chat Types = `{Fact, Decision, Constraint, Outcome, Summary}`; MCP Types не трогать; handoff Event-line остаётся last resort вне Types.

## Gap C — inbox/tree смешивают эпизоды и wiki

**Симптом:** catalog/inbox может показывать non-wiki шум; дерево не отделяет «compiled wiki» от «сырого inbox».  
**Против ai-memory:** wiki tree = compiled pages; inbox ≠ transcript dump.  
**Нужно:** catalog inbox = non-Event без wiki metadata; tree/compiled = только wiki metadata; SPA home = существующий `GET /api/memory-reader/tree` (без нового route).

## Gap D — Web vs MCP могут смотреть в разные store

**Симптом:** совпадение `ActiveMemoryCount` ≠ доказательство одного store.  
**Против ai-memory:** один workspace/project continuity по design.  
**Нужно:** процедура сверки path+actor+tenant; unify **только** после явного подтверждения env (открытый Q7).

## Открытый вопрос Q7

Совпадают ли `AGMEMORY_*` (MCP) и Web `MemoryGraph` / `MemoryReader` по **path + actor + tenant** в типичном локальном setup?  
Пока Q7 не закрыт фактом пользователя/env — волна D не «чинит конфиг молча».

## Вне скоупа gaps (не раздувать план)

- Новые MCP tools.
- Vector/RRF как DoD.
- Production auth / multi-writer.
- Type-priority ranking, `chatAligned`.
