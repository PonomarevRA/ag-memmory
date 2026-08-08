# Фаза 1 — открытые вопросы и проблемы для доработки

> Владелец файла: `phase1_extraction`  
> Статус: требуется действие release/platform owner вне этого репозитория.

## P1 — external release and consumer verification

| ID | Проблема | Почему не закрыта фазой | Следующее действие / владелец |
| --- | --- | --- | --- |
| P1-01 | Не утверждён private NuGet feed и способ аутентификации. | Репозиторий может собирать и проверять `.nupkg` в локальном credential-free feed, но не может публиковать пакет или хранить вымышленные credentials. Это соответствует нерешённому P0-04. | Release/platform owner утверждает registry, publisher identity, access/rollback policy и выполняет publish согласованной версии. |
| P1-02 | AGM ещё не переведён на опубликованные version-pinned `PackageReference`s. | Изменение AGM и создание release не входят в Phase 1 extraction; без опубликованной версии невозможно честно проверить сборку AGM против released package. | После P1-01 владелец AGM заменяет `ProjectReference`s, собирает AGM с точными версиями пакетов и фиксирует результат в release evidence. |

## Не является проблемой этой фазы

`AgMemory.*` contracts/core, LanceDB и миграция данных намеренно не добавлялись:
они относятся к следующим фазам и не должны менять legacy API при extraction.
