# Work-package C: Compiled wiki SPA / inbox

## Цель

Catalog **inbox** = non-Event без wiki metadata; **tree/compiled** = только записи с wiki metadata; SPA home использует существующий `GET /api/memory-reader/tree` (**без** нового route).

## Зачем (vs ai-memory)

Разделить «compiled wiki» и «сырой inbox», чтобы UI не выглядел как dump эпизодов (как у них pages ≠ observations dump).

## In

- Правила eligibility после волны A.
- Inbox query: исключить Event; показать non-wiki (без metadata) candidates по существующим API контрактам.
- Tree/compiled: только wiki metadata.
- Client home: tree endpoint as today; при необходимости уточнить labels/copy (RU).
- Тесты Reader catalog/tree + client page tests.

## Out

- Новый HTTP route для «wiki home».
- MCP write_page / markdown export.
- Graph redesign.
- Promise.all(tree+catalog) regression (уже запрещено baseline).

## Задачи

1. После merge A проверить catalog eligibility границы.
2. Развести inbox vs tree filters в `MemoryReaderCatalogQueryService` / LanceDb wiki paths.
3. SPA: убедиться, что home = tree; inbox отдельный UX path без Event.
4. Тесты: Event не в inbox; wiki-only в tree; non-wiki non-Event в inbox.
5. Краткая UX-копия: что такое inbox vs wiki.

## Критерии готово

- [ ] Event не в catalog inbox.
- [ ] Tree/compiled только wiki metadata.
- [ ] Нет нового route; home на `/api/memory-reader/tree`.
- [ ] Ready-path perf регрессий нет (нет full source scan на Ready).
- [ ] Тесты зелёные.

## Зависимости

**После A.** Независимо от B (желательно после B для согласованного канона Event, но не блокер).

## Риски

- «Без metadata» определение размыто — зафиксировать поле/флаг wiki metadata в Contracts/Core до кода UI.
- Пользователи ждут Event в reader — явно Out; Event смотреть через chat/status counts, не как wiki page.

## Non-goals

Markdown file wiki; CDN; Three.js graph changes.
