# P1.2. Wiki, теги, поиск и дерево с учётом области

**Владелец:** Core + Storage + Web
**Зависимости:** P1.1
**Статус:** completed

## Цель

Разделить одинаковые теги и страницы разных областей во всех reader-проекциях.

## Сделать

- Формировать facet identity как `area + normalized tag`, сохранив безопасные label/locator.
- Применить selector к catalogue, tags, search, tree и документу; сохранить защищённую пагинацию.
- Показывать выбранную область и сбрасывать независимые stream tokens при смене.

## Приёмка

Одинаковый тег в двух областях не смешивается; есть тесты переключения и пагинации tree/tags/search.

## Результат

- Tag и tree continuations содержат server-only immutable generation и `AreaId`; токен другой области, старого поколения или старой схемы возвращает `changed` и не открывает storage.
- Browser DTO не раскрывают generation или scope. Все document, tree, relation, inline и catalog links несут безопасный `?area=<opaque-id>`; area из deep link принимается только после сверки с `/areas`.
- При смене области или фильтра reader увеличивает monotonic epoch: старый ответ не может добавить документы, дерево или теги в новый экран.
- Проверки: `MemoryReaderEndpointTests` (18), `page-controller.test.ts` (7), полный client build/test (33); `git diff --check` чисто.
