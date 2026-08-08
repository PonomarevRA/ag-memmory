# Фаза 1 — выделение legacy-модуля

> Владелец: `phase1_extraction`  
> Статус: локальное выделение и package validation завершены; публикация и
> проверка AGM против released packages ожидают владельца release/platform.

## Итог

В `ag-memmory` создано независимое solution `ag-memory.slnx`. Три текущих
AGM-проекта и их тесты перенесены в явный временный слой
`Compatibility/`, поэтому будущие `AgMemory.*` проекты можно добавлять рядом
без смешения двух API. Никакие исходники AGM не изменялись.

| Legacy package / public namespace | Новый путь | Поддерживаемые TFM |
| --- | --- | --- |
| `Agm.Memory.Abstractions` / `Agm.Memory.Abstractions` | `src/Compatibility/Agm.Memory.Abstractions` | `net8.0`, `net10.0` |
| `Agm.Memory` / `Agm.Memory` | `src/Compatibility/Agm.Memory` | `net8.0`, `net10.0` |
| `Agm.Memory.Sqlite` / `Agm.Memory.Sqlite` | `src/Compatibility/Agm.Memory.Sqlite` | `net8.0`, `net10.0` |
| legacy test suite | `tests/Compatibility/Agm.Memory.Tests` | `net10.0` |

Public package IDs, assembly names, root namespaces, source behavior and
test code сохранены. Изменения в `.csproj` только переводят direct external
dependencies в central package management и корректируют относительные пути
после переноса. Сверка исключая `.csproj`, `bin` и `obj` подтвердила
byte-for-byte совпадение migrated source files с исходным модулем.

## Воспроизводимая проверка

В корне репозитория выполнено:

```bash
dotnet test ag-memory.slnx -c Release
dotnet pack ag-memory.slnx -c Release --no-build -o artifacts/local-feed
```

Результаты:

- `dotnet test`: 55 passed, 0 failed, 0 skipped (`Agm.Memory.Tests`, `net10.0`).
- `dotnet pack`: созданы `Agm.Memory.Abstractions.1.0.0.nupkg`,
  `Agm.Memory.1.0.0.nupkg` и `Agm.Memory.Sqlite.1.0.0.nupkg`.
- Проверка `.nuspec` подтверждает сохранение трёх package IDs и согласованные
  версии `1.0.0` для внутренних зависимостей; external dependencies имеют
  явно заданные версии из `Directory.Packages.props`.

## Package strategy

`Directory.Packages.props` централизует и запрещает локальное переопределение
версий direct external dependencies. `NuGet.Config` оставляет NuGet.org для
этих dependencies и вводит credential-free `artifacts/local-feed` только для
`Agm.Memory.*` packages. Инструкция для локального потребителя и модель
перехода на утверждённый private feed находятся в [`README.md`](../README.md).

`artifacts/local-feed` — validation/staging directory, а не опубликованный
registry. Файлы `.nupkg` игнорируются, чтобы результат release не был ошибочно
принят за уже выпущенный пакет.

## Не закрытые внешние критерии

Эта фаза намеренно не публикует пакеты и не меняет AGM. Следовательно,
невозможно заявить, что AGM уже собирается против released packages. Нужные
действия и владельцы зафиксированы отдельно в
[`phase-1-open-problems.md`](phase-1-open-problems.md).

## Handoff для QA и Phase 2

- Решение: `ag-memory.slnx`.
- Основная проверка: `dotnet test ag-memory.slnx -c Release`.
- Package check: `dotnet pack ag-memory.slnx -c Release --no-build -o artifacts/local-feed`.
- Новые canonical projects следует добавлять под `src/AgMemory.*`; не следует
  переименовывать или редактировать `src/Compatibility/Agm.Memory.*` в рамках
  Phase 2, пока migration strategy не предусматривает совместимый переход.
