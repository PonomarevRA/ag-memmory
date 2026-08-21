# Документация проекта AgMemory

## Назначение

`ag-memmory` — standalone .NET-решение для постепенного выделения памяти из
AGM в самостоятельный продукт **AgMemory**. Оно сохраняет совместимость с
устаревшими пакетами `Agm.Memory.*`, а также содержит новый локальный runtime:
хранилище памяти, Web-интерфейс и stdio MCP-сервер для агентов.

Проект рассчитан прежде всего на локальную работу: память, настройки модели и
браузерный интерфейс остаются на машине пользователя. Это не готовая
production-система идентификации или общего доступа между произвольными
процессами.

## Состав решения

| Часть | Ответственность |
| --- | --- |
| `src/AgMemory.Contracts` | Контракты записей, областей видимости, команд и запросов. |
| `src/AgMemory.Core` | Авторизация exact scope, обработка команд, поиск, построение контекста и hot memory. |
| `src/AgMemory.Storage.LanceDb` | Локальное хранилище LanceDB, поиск, граф и wiki-представления. |
| `src/AgMemory.Web` | Loopback Web-host и русскоязычный Vite SPA: чат, статус памяти, граф и reader. |
| `src/AgMemory.McpServer` | Локальный MCP over stdio для Codex, Cursor и Claude. |
| `src/Compatibility/Agm.Memory*` | Временный совместимый слой с прежними пакетами AGM. |
| `tests/` | Модульные, контрактные, storage-, Web- и интеграционные проверки. |

Точка входа решения — `ag-memory.slnx`; закреплённый SDK описан в
`global.json`, а версии прямых пакетов — в `Directory.Packages.props`.

## Основные потоки

### Сохранение

1. Клиент или локальный чат формирует `RememberCommand` с актором и exact
   scope.
2. `MemoryCommandService` в `src/AgMemory.Core` последовательно выполняет
   проверку доступа, редактирование входа, идемпотентность и транзакционную
   запись.
3. `AgMemory.Storage.LanceDb` сохраняет запись в локальном store.

Локальный чат Yuki сохраняет вопрос и завершённый ответ как события. Его
реализация находится в
`src/AgMemory.Web/Features/Chat/LocalChatMemoryFeature.cs`.

### Поиск и контекст

`MemoryQueryService` сначала авторизует запрошенную область, затем выполняет
гибридный поиск и строит ограниченный контекст. Для локального чата установлен
лимит: до восьми результатов и бюджет 800 токенов. Если контекст пуст, чат
может использовать последнюю активную summary/event-запись как handoff.

### Web-интерфейс

`AgMemory.Web` выдаёт статические файлы SPA и локальное API. В Development
модель по умолчанию — локальный Small Yuki на loopback. Токен защиты от
подделки запроса получается с `/api/antiforgery`; секреты и параметры модели не
попадают в Vite-сборку.

Страницы `/memory-status` и `/memory-graph` служат локальной диагностикой.
Они не передают в браузер текст записей, scope, сущности или постоянные
идентификаторы: граф использует response-local opaque IDs и агрегированные
данные.

### MCP для агентов

`src/AgMemory.McpServer/Program.cs` запускает MCP по stdio. В нём намеренно
доступны только три инструмента:

| Инструмент | Действие |
| --- | --- |
| `memory_recall` | Возвращает активные совпадения фиксированной области. |
| `memory_remember` | Сохраняет компактную запись. |
| `memory_status` | Показывает доступность store и число активных записей. |

Путь хранилища, actor и scope задаются окружением процесса MCP, а не входными
параметрами инструмента. Для совместно используемой памяти у клиентов должны
совпадать path и scope; `AGMEMORY_CLIENT_ID` должен различаться, чтобы
сохранялось происхождение записи.

## Хранение и границы данных

- Основное локальное хранилище — LanceDB.
- Для macOS standalone данные и `appsettings.local.json` располагаются вне
  заменяемого `.app`: по умолчанию в
  `~/Library/Application Support/AgMemory/`.
- Web-host и диагностические страницы ограничены Development/loopback.
- Регистрация MCP не является production-моделью авторизации и не обещает
  безопасность конкурентной записи несколькими процессами.
- В текущем local runtime используется `lexical-fallback`; семантические
  embeddings не заявляются как доступные по умолчанию.

## Запуск и проверка

Из корня репозитория:

```bash
dotnet test ag-memory.slnx -c Release
dotnet run --project src/AgMemory.Web/AgMemory.Web.csproj
```

Для точечной проверки SPA используйте `npm test` и `npm run build` в
`src/AgMemory.Web/client`. Для сборки локальных совместимых пакетов:

```bash
dotnet pack ag-memory.slnx -c Release --no-build -o artifacts/local-feed
```

macOS bundle собирается скриптом `scripts/package-macos-app.sh`.

## Известные ограничения и открытые границы

- Совместимый слой `Agm.Memory.*` существует для плавной миграции и не
  означает, что AGM ссылается на проекты этого репозитория.
- Local Web/MCP сценарий не заменяет production identity-to-scope policy.
- При намеренном обмене памятью между Web и MCP необходимо отдельно проверить
  совпадение path, actor и всех значений exact scope. Равенство счётчиков само
  по себе не доказывает использование одного store.
- Публикация пакетов во внешний приватный feed остаётся процессом release
  owner; локальный `artifacts/local-feed` не содержит credentials.

## Наблюдения по AGM в ходе подготовки

Это наблюдения о среде оркестратора, а не дефекты AgMemory.

| Наблюдение | Подтверждённый эффект |
| --- | --- |
| Codex runner доступен; Core и runner сообщили `healthy`. | Задачу можно направлять Codex через AGM. |
| Claude CLI недоступен, OpenRouter отключён либо не имеет ключа. | Эти провайдеры нельзя выбрать для данной задачи без настройки среды. |
| Write root требует явного namespace с ReadWrite alias. | Для создания MD должен передаваться alias `write`; read-only root недостаточен. |
| Запрос на подготовку этого документа был принят AGM, но после четырёх штатных ожиданий по 60 секунд не вернул ответ или итоговый статус; в последнем ответе чата `jobId` уже отсутствовал. | Документ подготовлен локальным резервным путём. Состояние следует воспроизвести и диагностировать в AGM: выполнить задачу с тем же workspace и проверить жизненный цикл job/message. |

## Основные источники

- `README.md`
- `src/AgMemory.Core/Commands/MemoryCommandService.cs`
- `src/AgMemory.Core/Retrieval/MemoryQueryService.cs`
- `src/AgMemory.Web/Features/Chat/LocalChatMemoryFeature.cs`
- `src/AgMemory.McpServer/Program.cs`
- `src/AgMemory.McpServer/AgMemoryTools.cs`
- `docs/memory/current-state.md`
