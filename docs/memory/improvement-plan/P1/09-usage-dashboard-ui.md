# P1.9. Dashboard использования памяти

**Владелец:** Web
**Зависимости:** P1.8
**Статус:** planned

## Цель

Показать прозрачную сводку использования памяти агентами за сутки.

## Сделать

- Показать period, operations, client label, query descriptor, returned count и delivered token estimate.
- Вывести источник данных, estimator version и formula/baseline; при их отсутствии показать «не рассчитано».
- Добавить loading/empty/error states, pagination и no-store поведение.

## Приёмка

UI тестами подтверждает подписи метрик, отсутствие raw данных и корректное состояние без baseline.
