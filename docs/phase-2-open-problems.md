# Фаза 2 — открытые вопросы и follow-up

> Владелец файла: `phase2_contracts_core`  
> Правило: это не список скрытых дефектов реализации. Здесь только решения,
> которые нельзя безопасно угадать в Domain/Core.

## TD-P2-01 — реализовано техническое уточнение `MemoryRecord.Reason`

- **Статус:** решено технически; проверить при optional importer work.
- **Форма:** public `MemoryRecord.Reason` — nullable `string?` в
  `AgMemory.Contracts`. Если оно supplied, Core отправляет его в
  `IIngressRedactor` вместе с canonical text; raw value не помещается в outbox
  message или idempotency receipt.
- **Причина:** legacy `workspace_memory.reason` должен иметь переносимое
  destination, не перегружая provenance. Это не утверждает legacy migration
  как обязательную и не задаёт retention policy.
- **Follow-up / owner:** migration owner mapping preserves the field only
  after source-data approval and documents redaction transformations.

## P2-01 — реальная scope authority остаётся внешней policy

- **Статус:** blocked by P0-01.
- **Факт:** Core использует только `IAuthorizationScopeValidator` и точные
  selectors, а tests используют fixed synthetic grants.
- **Не делать:** нельзя выводить tenant/project из workspace/chat/run или
  добавлять workspace fallback без явного selector от policy.
- **Следующий владелец:** product/security owner утверждает mapping и source
  authority; затем host/client owner предоставляет production implementation
  validator-а.

## P2-02 — retention/delete/legal hold не выбраны

- **Статус:** blocked by P0-02.
- **Факт:** `ForgetAsync` возвращает `PolicyNotConfigured` без mutation, пока
  `IRetentionPolicy` не разрешит operation. Expired data технически не
  возвращается, но physical purge job не реализован.
- **Следующий владелец:** privacy/data owner defines versioned delete, hold and
  purge policy; storage owner implements approved durable mechanics.

## P2-03 — embedding contract не выбран

- **Статус:** blocked by P0-03.
- **Факт:** Core calls `IEmbeddingProvider` only with an explicit
  `EmbeddingContract`; synthetic tests use a fake two-dimensional contract.
  Это не рекомендация provider/model/dimension/normalization.
- **Следующий владелец:** ML/platform owner approves a versioned contract;
  storage owner rejects or partitions incompatible records/indexes.

## P2-04 — storage boundary не доказывает durable adapter

- **Статус:** open, owner Phase 3 Storage Abstraction / adapter.
- **Факт:** test-only `InMemoryStore` proves Core transaction semantics, but
  does not prove restart recovery, concurrent commits, outbox delivery,
  filtering pushdown or engine failure handling.
- **Следующий владелец:** storage owner supplies a durable implementation and
  adapter integration suite without changing the Contracts/Core dependency
  firewall.

## P2-05 — future host boundaries need repeated redaction tests

- **Статус:** open, owner Client/MCP/host phases.
- **Факт:** Core redacts before its storage/outbox transaction and has no
  logging dependency. A future transport, adapter or telemetry sink can still
  introduce a plaintext path if it bypasses command services.
- **Следующий владелец:** every host/adapter test suite repeats a raw-secret
  negative test for request parsing, diagnostics and delivery metadata.
