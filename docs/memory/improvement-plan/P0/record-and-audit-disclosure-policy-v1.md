# Policy RDP-001. Раскрытие записей и аудита

**Версия:** 1.0
**Статус:** принято для P1
**Дата:** 2026-08-22
**Владелец:** security reviewer + Web/Core

## Назначение и граница

Политика задаёт единственный allowlist для будущих record browser и usage-audit API/UI. Она не меняет существующий reader: его контентная выдача остаётся отдельной локальной функцией. Любое поле `MemoryRecord` и любое поле будущего usage event, которого нет в этой таблице, запрещено по умолчанию. Policy не утверждает реальные значения, владельцев или production-конфигурацию.

Доступ к обоим будущим интерфейсам допускается только после P0.5: включён отдельный feature flag, `Development`, loopback, server-configured authority и ответ с `Cache-Control: no-store`. Выключенный флаг, не-loopback или отсутствие конфигурации возвращают нейтральный unavailable/404-ответ без различимого существования данных.

## Матрица record browser

| Данные | Решение | Условие / преобразование | Обязательный тест |
| --- | --- | --- | --- |
| Opaque `href` или server-issued row key | allow | Не durable ID, не содержит scope, actor, storage path или route key; годен только в пределах текущей локальной сессии/API. | Значение нельзя сопоставить с `MemoryId` и нельзя использовать для cross-scope чтения. |
| `type`, title, namespace, tags, ограниченный preview, `updatedAt` | allow | Только в безопасном DTO; preview имеет фиксированный лимит и не является raw canonical text. | JSON содержит только allowlist; UI рендерит именно DTO. |
| Счётчики связей / агрегации | allow | Только bounded count; без идентификаторов соседей, если это не существующая безопасная wiki-навигация. | Нет IDs и raw text соседних записей. |
| Provenance client label | conditional | Только короткий allowlisted label клиента, заданный конфигурацией; не `SourceSystem` из записи и не свободный текст. | Неallowlisted label не выдаётся; label не раскрывает user/host/path. |
| Canonical text и `Reason` | conditional | Только отдельный opt-in endpoint для локального Development+loopback после всех gates P0.5; не включать в list/search/audit и не кэшировать. | Без opt-in поля отсутствуют; любой закрытый gate не раскрывает текст. |
| `MemoryId`, deduplication key, version, route key, block token | deny | Никогда не выдаются browser/audit DTO. Внутренние защищённые navigation tokens не являются раскрываемыми полями. | Сериализованный ответ не содержит исходных значений или их имён. |
| `MemoryScope` и его пять raw IDs, actor | deny | Никогда не выдаются, не принимаются от browser как доверенный selector. | Попытка передать scope/actor не изменяет authority; ответ их не содержит. |
| storage path, host/configuration, datasource | deny | Никогда не выдаются. | Нет полей/строк пути и конфигурации в API/UI. |
| provenance, evidence refs, message/execution IDs | deny | Исключение — только conditional allowlisted client label выше. | Нет source IDs, evidence или approved metadata. |
| embedding reference/vector/content hash, entities, importance/confidence/token cost, lifecycle/internal timestamps | deny | Не являются browser-метаданными в P1. | DTO не содержит эти поля; поиск и сортировка не принимают их как вход. |

## Матрица usage audit

| Данные | Решение | Условие / преобразование | Обязательный тест |
| --- | --- | --- | --- |
| Время, разрешённый `clientLabel`, optional `areaId`, operation, outcome, delivered-token estimate, `estimationMethodVersion` | allow | `clientLabel` приходит только из allowlist серверной конфигурации, не actor ID/user/host/path. `UsageAuditEvent` P1.5 принимает `areaId` лишь как structurally valid opaque slug и не зависит от Web-каталога. Emitter’ы P1.6/P1.7 до создания/сериализации события сверяют его с server-configured P1.1; неизвестное значение отклоняют либо не передают. Метка optional, non-authoritative и не содержит raw scope. Значения и округление определяются P0.3. | Ответ не содержит direct identifiers; неallowlisted label и неизвестный area не сериализуются; агрегаты используют только разрешённые поля. |
| Query descriptor | conditional | До отдельного выбора P0.3 не записывать. После выбора — только TTL-redacted text **или** salted hash + length по принятому контракту. | Event без утверждённого descriptor не пишет query; raw query не выдаётся. |
| `potentialTokensSaved` | conditional | Только после утверждения formula и baseline в P0.3; помечать как estimate, не billing и не факт экономии. | При отсутствии baseline поле отсутствует, не равно выдуманному нулю. |
| Raw query, prompt, response/context text | deny | Никогда не записываются и не выдаются audit API/UI. | Логи/DTO не содержат переданный текст. |
| Memory IDs, raw scope, actor, user/client/session/run IDs, storage path | deny | Никогда не записываются и не выдаются. `clientLabel` и `areaId` не являются исключением: они не принимают raw scope и не дают authority для выбора данных. | Событие и API не содержат этих полей; входящие значения игнорируются/отклоняются. |
| Provider usage/billing/cost | deny | Не подменять estimated tokens платёжными данными. | Тексты UI/API и имена полей не заявляют billing/cost. |

## Правила реализации и проверки

1. Каждый новый DTO начинается с явного набора полей этой policy; запрещены generic serialization `MemoryRecord`, audit event и extension-data.
2. Browser search использует только allowlisted title/namespace/tags/preview. До выбора P0.3 raw query не попадает ни в audit storage, ни в логи.
3. API-тесты проверяют положительный allowlist и отрицательный набор: JSON не содержит `id`, `scope`, `actor`, `storage`, `provenance`, `evidence`, `embedding`, `canonicalText`, `reason` (кроме выделенного opt-in endpoint) и raw query. UI-тесты проверяют, что скрытые поля не попадают в DOM, URL, telemetry и client state.
4. Security regression проверяет Development+loopback+flag+server authority+`no-store` для каждого endpoint; отсутствие любого условия не раскрывает данные.

## Источники подтверждённых текущих паттернов

- `src/AgMemory.Contracts/Domain.cs`: raw `MemoryRecord` содержит scope, provenance, embedding и durable ID; поэтому он unsafe-by-default.
- `src/AgMemory.Web/Features/MemoryReader/MemoryReaderEndpoint.cs`: текущий catalog DTO ограничен href, type, title, namespace, tags, preview и updatedAt и намеренно не выдаёт scope/actor/provenance/durable IDs.
- `src/AgMemory.Web/Features/MemoryReader/MemoryReaderAccessPolicy.cs`: reader уже ограничен Development+loopback; endpoint устанавливает `no-store`.
