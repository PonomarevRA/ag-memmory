# Work-package B: Typed inject для Yuki

## Цель

Chat Recall Types = `{Fact, Decision, Constraint, Outcome, Summary}`; лимиты **8 + 800**; MCP Types **не** менять. Handoff (Summary else Event) при empty recall остаётся last resort **вне** Types.

## Зачем (vs ai-memory)

Authority-aware recall: устойчивые типы (Fact/Decision/…) раньше episodic Event. Event не должен занимать inject slots и сжигать token budget.

## In

- `LocalChatMemoryFeature` (или эквивалент): явный Types set для chat inject.
- Сохранить SearchLimit=8, TokenBudget=800.
- Handoff-line только когда ranked recall пуст.
- Тесты: Event не в ranked inject; Fact/Decision попадают; empty → handoff; MCP path не затронут.
- Канон: `K=1` ≠ Event eligible.

## Out

- Менять MCP `memory_recall` default Types.
- Type-priority ranking внутри Types.
- Увеличение бюджета «чтобы влезло больше Event».
- Новые MCP tools / handoff_accept.

## Задачи

1. Зафиксировать enum/set Types в chat inject code path.
2. Убедиться, что handoff не добавляет Event в ranked list.
3. Обновить Web/Core tests на inject filtering.
4. Проверить memory-status `lastInjectHitCount` семантика без изменения privacy.
5. Краткая заметка в README/local docs: chat Types vs MCP Types.

## Критерии готово

- [ ] Chat inject не возвращает Event в ranked hits.
- [ ] 8 + 800 соблюдены.
- [ ] Empty recall → handoff Summary else Event.
- [ ] MCP recall поведение без регрессии в тестах MCP/status.
- [ ] Тесты зелёные.

## Зависимости

Может идти **параллельно с A**. Не зависит от C/D.

## Риски

- Слишком узкий Types set → пустой inject чаще → чаще handoff (приемлемо; не расширять Types Event).
- Путаница «Summary в Types и в handoff» — Summary в Types ranked; handoff Summary только если ranked пуст.

## Non-goals

Semantic re-rank; entity boost в inject; изменение browser transcript privacy.
