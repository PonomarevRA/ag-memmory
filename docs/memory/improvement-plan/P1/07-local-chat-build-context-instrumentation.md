# P1.7. Аудит BuildContext локального чата

**Владелец:** Web + Core
**Зависимости:** P1.5
**Статус:** planned

## Цель

Фиксировать формирование контекста памяти для локального чата.

## Сделать

- Эмитить один event после `BuildContext` с budget, selected/omitted count и EstimatedTokenCost.
- Не выдавать оценку за фактический billing; сохранять версию estimator и outcome.
- Изолировать area/scope по P1.1 и не дублировать событие при повторном рендере UI.

## Приёмка

Тесты LocalChatMemoryFeature подтверждают ровно одно корректно scoped событие на запрос.
