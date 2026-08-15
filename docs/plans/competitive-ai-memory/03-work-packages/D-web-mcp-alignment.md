# Work-package D: Web ↔ MCP alignment

## Цель

Процедура сверки: Web `ActiveMemoryCount` vs MCP `memory_status.activeCount`. Совпадение счётчика **недостаточно**. Unify path/actor/tenant **только** после явного подтверждения (закрытие **Q7**).

## Зачем (vs ai-memory)

Cross-agent continuity требует одного workspace/store. Иначе Cursor MCP и Yuki Web «не делят память», и токены/ожидания расходятся.

## In

- Чеклист в docs: сравнить `AGMEMORY_STORAGE_PATH` / actor / tenant с Web `MemoryGraph`/`MemoryReader` options.
- Использовать уже существующий `readerAligned` + status UI; не тащить секреты/scope в браузер сверх текущего контракта.
- Опционально: dev-only server log/compare helper **без** расширения MCP tools.
- Документ «как выровнять env» для Cursor registration (пример из README).

## Out

- Молчаливая смена production defaults.
- Новые MCP tools.
- Shared-server auth.
- Утверждение «counts match ⇒ aligned» без path/actor/tenant.

## Задачи

1. Зафиксировать Q7 как checkbox в [criteria](../04-acceptance/criteria.md).
2. Написать процедуру: status UI → MCP `memory_status` → сравнить path/actor/tenant из **server/env**, не из browser payload.
3. Если Q7=yes: один согласованный пример env + appsettings.local для shared store.
4. Если Q7=no: документ «два store» + как не путать диагностику.
5. Тест/док-тест: mismatch scenario не помечается aligned.

## Критерии готово

- [ ] Процедура опубликована в этом пакете / README pointer.
- [ ] Q7 закрыт фактом (yes/no + значения).
- [ ] Unify выполнен **только** при Q7=yes.
- [ ] `readerAligned` семантика не ослаблена.
- [ ] Нет утечки scope/текстов в browser.

## Зависимости

После **A** (стабильный catalog не маскирует «ложный» progress). Можно после B. **Q7** — human gate.

## Риски

- Разные actor id при общем path — provenance ок, но exact scope должен совпадать для shared recall.
- Пользователь сравнивает только counts — процедура должна явно запретить этот shortcut.

## Non-goals

Multi-tenant SaaS; changing `AGMEMORY_CLIENT_ID` semantics; Codex-specific defaults (Codex сейчас недоступен по запросу).
