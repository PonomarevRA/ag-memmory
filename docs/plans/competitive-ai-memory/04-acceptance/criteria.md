# Acceptance criteria пакета competitive-ai-memory

## Продуктовый DoD (после реализации A–D)

1. **Токены:** Yuki ranked inject не включает Event; бюджет 8+800; handoff только при empty ranked.
2. **Wiki:** Persist(Event) не инвалидирует catalog generation; Event ∉ catalog/inbox; tree = wiki metadata.
3. **SPA:** home на существующем tree API; Ready-path без full source scan regression.
4. **Continuity:** Q7 закрыт; Web↔MCP либо выровнены, либо явно задокументированы как два store.
5. **Границы:** MCP остаётся 3 tools; нет git-md primary; нет production auth в этом пакете.

## Чеклист по волнам

### A

- [ ] Event persist ≠ generation invalidate
- [ ] Event ∉ catalog eligibility
- [ ] Chat Remember(Event) сохранён
- [ ] Тесты

### B

- [ ] Types = Fact, Decision, Constraint, Outcome, Summary
- [ ] 8 + 800
- [ ] Handoff вне Types
- [ ] MCP Types untouched
- [ ] Тесты

### C

- [ ] Inbox = non-Event without wiki metadata
- [ ] Tree/compiled = wiki metadata only
- [ ] No new route
- [ ] Тесты + Ready-path

### D

- [ ] Процедура сверки path+actor+tenant
- [ ] Q7 answered
- [ ] Unify only if Q7=yes
- [ ] Counts-only shortcut rejected

## Q7 (human)

| Поле | Значение |
| --- | --- |
| Shared path? | _TBD_ |
| Shared actor? | _TBD_ |
| Shared tenant? | _TBD_ |
| Verdict | _yes / no_ |

## Регрессии, которые нельзя принимать

- Возврат `Promise.all(tree+catalog|document)` на wiki page.
- Расширение MCP tool surface.
- Утечка memory text / scope / entity names в `/memory-status` или graph browser payload.
- Тип-приоритет ranking или `chatAligned` «вместо» Types filter.

## Статус плана (документы)

- [x] 11 MD в `docs/plans/competitive-ai-memory/`
- [ ] Реализация A–D (отдельный запрос; этот пакет = план)
