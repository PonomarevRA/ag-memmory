# Work-package A: Event и catalog generation

## Цель

`Persist(Event)` **не** инвалидирует catalog/wiki generation; `Event` **не** входит в catalog eligibility. Chat по-прежнему `Remember`ит Event каждый turn.

## Зачем (vs ai-memory)

У ai-memory сырой эпизод не становится wiki page по умолчанию; wiki — compiled. В AgMemory chat Event не должен сбрасывать Ready compiled generation и засорять catalog как «страницу».

## In

- Правило invalidate: Event-write ≠ catalog generation bump.
- Правило eligibility: Event ∉ catalog leaf / wiki metadata set.
- Тесты: Persist Event → generation id/stable; Event не появляется в catalog query eligibility.
- Документировать канон Event (см. adopt-reject).

## Out

- Менять MCP Types / recall API.
- Убирать `Remember(Event)` из chat.
- Type-priority, vector, новые MCP tools.
- Полный redesign rebuild policy за пределами Event.

## Задачи

1. Найти путь invalidate catalog на Persist (Core + LanceDB reader store).
2. Исключить Event из триггера invalidate generation.
3. Исключить Event из catalog eligibility / facet source для wiki catalog.
4. Unit/integration: chat Event persist + catalog generation stable; Event not in catalog.
5. Регрессия: Fact/Decision/Summary persist по-прежнему обновляет generation при необходимости.

## Критерии готово

- [ ] После N chat turns catalog generation не пересобирается только из-за Event.
- [ ] Event отсутствует в catalog eligibility.
- [ ] Chat transcript persistence (Event) сохранена.
- [ ] Тесты зелёные на затронутых suites.

## Зависимости

Нет (стартовая волна). Блокирует **C**.

## Риски

- Слишком широкое «не invalidate» может пропустить нужный bump для других типов — ограничить **только Event**.
- Путаница handoff Event vs catalog Event — держать канон в тестах/доке.

## Non-goals

Удаление Event из модели; запрет Remember Event; изменение UI status counters semantics без согласования.
