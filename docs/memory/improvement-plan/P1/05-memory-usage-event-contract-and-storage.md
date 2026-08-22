# P1.5. Контракт и хранилище событий использования

**Владелец:** Contracts + Core + Storage
**Зависимости:** P0.3, P0.5, P1.1
**Статус:** completed

## Цель

Добавить append-only, privacy-gated историю использования памяти.

## Сделать

- Ввести versioned `UsageEvent`: время, операция, pseudonymous client label, safe area, query descriptor, результаты, delivered estimate и outcome.
- Хранить estimator/baseline version и retention; запретить raw scope, durable IDs и неразрешённый query text.
- Сделать запись независимой от успеха аудита и строго определить success/failure semantics.
- Ограничить `areaId` в Contracts/Core структурной проверкой safe opaque slug: P1.5 не читает и не зависит от Web-каталога областей P1.1.
- Зафиксировать для P1.6/P1.7 обязанность server-side проверить принадлежность slug настроенному каталогу P1.1 **до** создания и сериализации события; неизвестная область там отклоняется либо становится отсутствующим `areaId`.

## Приёмка

Миграция и тесты подтверждают append-only, один event на операцию и безопасную деградацию.

## Результат

- В `AgMemory.Contracts` добавлены versioned `UsageAuditEvent`, закрытые allowlist-перечисления и append-only порт без read API.
- `UsageAuditEventFactory` из Core создаёт HMAC-SHA-256 descriptor и длину временного query; raw query в контракт и LanceDB не попадает.
- LanceDB добавляет и проверяет `usage_audit_events` через schema manifest: идентичный `eventId` идемпотентен, конфликтующий отклоняется, cleanup удаляет только записи строго старше заданного UTC cutoff.
- Целевые тесты покрывают HMAC/estimate, schema privacy, append/idempotency/conflict и boundary cleanup.
- Тесты P1.5 покрывают допустимый/недопустимый формат optional opaque slug, но не членство в P1.1. Последнее — отдельная приёмка emitter’ов P1.6/P1.7; ни один неизвестный slug не попадает в хранилище.
