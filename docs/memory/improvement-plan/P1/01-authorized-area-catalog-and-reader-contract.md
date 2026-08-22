# P1.1. Каталог разрешённых областей и reader contract

**Владелец:** Core + Web
**Зависимости:** P0.1, P0.5
**Статус:** completed

## Цель

Дать пользователю безопасный выбор области в границах утверждённой карты current exact scope.

## Сделать

- Добавить серверный каталог разрешённых area labels без raw scope и durable IDs.
- Привязать area к reader selector и opaque continuation; при смене сбрасывать список, дерево, теги и поиск.
- Отклонять area/cursor, не принадлежащие настроенной политике.

## Приёмка

Cross-area URL и continuation не раскрывают данные; переключение области не смешивает результаты.

## Результат

- `MemoryReader:Areas` задаёт server-owned id, label, storage, actor, home и exact scope; отсутствие списка сохраняет legacy `default` area.
- `/api/memory-reader/areas` возвращает только id, label и признак default. Area id ограничен opaque slug и не раскрывает scope, actor или storage.
- Все catalog/tree/tag/document/block continuations используют schema v3 с bound `area`; чужой или неизвестный area отклоняется до обращения к store.
- Reader selector сбрасывает все пагинированные потоки и сохраняет выбранную область для document/detail navigation.
