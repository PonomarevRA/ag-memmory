# P0.5. Флаги и границы доступа

**Владелец:** security reviewer + release owner
**Зависимости:** P0.2
**Статус:** implemented

## Цель

Не открыть browser и audit-данные за пределами локального Development-сценария.

## Сделать

- Назначить отдельные feature flags для record browser, исходного текста и usage dashboard.
- Зафиксировать loopback/Development guard, авторизацию и `no-store` для каждого endpoint.
- Описать безопасное поведение при выключенном флаге и при недоступности аудита.

## Приёмка

Есть exposure matrix «функция → флаг → разрешённая среда → отказ» и тесты закрытого доступа.

## Результат

- Добавлены серверные флаги и строгий guard: [MemoryExperienceOptions](../../../../src/AgMemory.Web/Features/MemoryExperience/MemoryExperienceOptions.cs) и [MemoryExperienceAccessPolicy](../../../../src/AgMemory.Web/Features/MemoryExperience/MemoryExperienceAccessPolicy.cs).
- Флаги явно выключены в [appsettings.json](../../../../src/AgMemory.Web/appsettings.json), не передаются в SPA и пока не включают маршруты или инициализацию хранилища.
- Проверки IPv4/IPv6 loopback, внешнего адреса, Production, выключенного флага и отсутствующего адреса находятся в [MemoryExperienceAccessPolicyTests](../../../../tests/AgMemory.Web.Tests/MemoryExperienceAccessPolicyTests.cs).

| Будущая функция | Флаг | Разрешение | Отказ |
| --- | --- | --- | --- |
| Browser записей | `RecordBrowserEnabled` | Только Development и IPv4/IPv6 loopback | 404 без инициализации хранилища, с `Cache-Control: no-store` |
| Исходный текст | `RecordBrowserEnabled` и `RawRecordTextEnabled` | Только Development и IPv4/IPv6 loopback | 404 с `Cache-Control: no-store`, без текста в ответе и до обращения к хранилищу |
| Usage dashboard | `UsageDashboardEnabled` | Только Development и IPv4/IPv6 loopback | 404 с `Cache-Control: no-store`, без данных аудита и до обращения к хранилищу |

Маршрутов в P0.5 нет: будущий endpoint обязан применить `Allows`, вернуть указанный отказ до обращения к store и не передавать значения флагов в клиент.
