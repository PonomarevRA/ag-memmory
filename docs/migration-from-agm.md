# Миграция из AGM в AgMemory

> **Migration card**
>
> - **Владелец дизайна:** `architecture`; **владелец исполнения:** `migration owner` в Phase 4.
> - **Статус:** proposed runbook. Он не разрешает доступ к production SQLite и не создаёт source-project dependency на AGM.
> - **Источник фактов:** [current AGM baseline](current-state-agm.md); структура `workspace_memory_settings`, `workspace_memory`, `workspace_memory_candidates`, `fanout_memory`, `fanout_memory_events`.
> - **Граница:** импорт работает только с export API или предоставленным SQLite snapshot, не с `ProjectReference` на AGM и не с общим runtime database connection.

## Preconditions — стоп-условия, а не предположения

До первой миграции реальных данных migration owner подтверждает все пункты:

1. P0-01 утвердил mapping `tenant/project/workspace/chat/run`, scope authority и export enrichment, чтобы у каждого целевого durable record был authorised `tenant`.
2. P0-02 утвердил retention/deletion/legal-hold, в том числе eligibility кандидатов и expired fan-out events.
3. P0-03 утвердил embedding provider/model/dimension/normalization/version и policy для records, которые должны попасть в vector index.
4. Контракт и LanceDB adapter прошли scope/idempotency/reopen tests; migration target schema и embedding contract versions опубликованы.
5. Export authority и путь к snapshot одобрены владельцем данных. В этот repository не копируют production database и raw production content.

Отсутствие любого precondition означает: разрешены тестовые fixtures, `--dry-run` на одобренной synthetic export и реализация CLI, но не import/cutover реальных данных. Package feed (P0-04) и решение про `mcp-ai-memory` (P0-05) не меняют mapping сам по себе, но блокируют соответственно Phase 1/7 rollout и должны быть видимы в release checklist.

## Входной export и manifest

CLI принимает ровно один из вариантов:

- immutable SQLite snapshot, созданный согласованным export workflow; или
- versioned export bundle/API response, который содержит те же logical rows.

Входной bundle включает format version, source system/version, UTC cut-off, schema version, table row counts, per-table SHA-256 checksums и mapping/policy/embedding version IDs. Manifest не содержит raw content, secrets или query text. Content hashes используются только для parity и должны быть salt/handling-compatible с privacy policy.

Каждая run создаёт target manifest с: source/target counts, counts по action (`created`, `already-imported`, `skipped-by-policy`, `failed`), row error references, per-table checksums, legacy-to-new ID map, source/target schema version, contract/embedding versions, redaction rule version, start/finish/elapsed time и CLI version. Error reference — opaque legacy row locator plus safe code; не plaintext.

## Нормативное отображение

| AGM data | AgMemory target | Что сохраняется | Eligibility / важное ограничение |
| --- | --- | --- | --- |
| `workspace_memory` | `MemoryRecord` типа `Summary` (или утверждённый `Fact`) | content после ingress redaction, reason, source version, timestamps, workspace/chat/run/message/execution provenance | требуется approved scope enrichment; миграция не создаёт отдельные chat/run records без source evidence |
| `workspace_memory_settings` | scope policy/enablement record/configuration | `enabled`, settings version, workspace identity | disabled **не** означает deleted; mapping policy указывает, кто применяет enablement |
| `fanout_memory` | `SessionHotMemory` | complete `(workspace,parent_chat,run)`, version, content после redaction, created/updated/expiry | импортируется только пока eligible under approved retention; original `ExpiresAt` не продлевается |
| `fanout_memory_events` | idempotency history | `run_id`, `event_key`, compatible command identity | импортируется только для ещё действующей hot-memory и если P0-02 это одобрил; иначе manifest `skipped-by-policy` |
| `workspace_memory_candidates` | нет default target | safe count/status/error metadata лишь в manifest | не импортируется без явной product retention decision; это retry queue, не canonical memory |

Необходимо сохранять legacy table name/key в `MemoryProvenance.LegacyRecordId` и ID map. У одного legacy workspace summary должен быть один устойчивый target identity на snapshot, а не новая запись при повторном запуске. Реализация может использовать migration ledger с уникальным `(source_system, source_table, legacy_primary_key, source_version_or_checksum)`; выбранный алгоритм target ID должен быть детерминирован или первоначально сохранён в ledger.

## Последовательность выполнения

```text
1. Inspect + validate export    2. Plan / dry run       3. Import resumable batches
   schema, checksums, policy       no target mutation       redact -> map -> upsert -> ledger
            |                             |                         |
            v                             v                         v
4. Validate only              5. Shadow retrieval      6. Feature-flag read cutover
   parity + replayability        AGM prompt unchanged       fallback configurable
            |                             |                         |
            +---------------------------> 7. Write cutover -> 8. archive/decommission
```

### 1. Inspect and validate

CLI validates allowed schema versions, required columns, all checksums, non-empty legacy IDs and cut-off consistency before touching target storage. It reports unknown table/column values as actionable validation errors. It does not “best effort” an unrecognised schema. It resolves target `MemoryScope` only through the approved mapping configuration, then invokes the same authorization/redaction/validation pipeline as normal ingestion.

### 2. `--dry-run`

Dry run performs schema validation, mapping, eligibility checks, canonicalisation, redaction detection and prospective counts. It may produce a plan manifest but must not create a target table, record, ledger entry, outbox delivery or embedding. It is the required review artifact before an import window.

### 3. Resumable import

Batch size is an explicit CLI option captured in the manifest. For each row, an atomic target transaction writes record/evidence/provenance, migration ledger and necessary idempotency history together. Commit order is deterministic by `(table priority, legacy primary key)`; a crash leaves either a committed ledger+record pair or neither.

Retries first consult the ledger and source checksum:

- matching source locator/checksum returns `already-imported`, never creates a second record;
- changed source checksum becomes an explicit `source-changed` replay item, not an overwrite;
- transient adapter failure leaves no completed ledger entry and is retryable;
- validation/redaction/mapping failure receives a safe error code and is replayable after policy/config repair.

Embeddings are generated only after content has passed ingress redaction. If the approved policy supports a non-vector record, the manifest records that intentional state; no undocumented default embedding model is used.

### 4. `--validate-only`

Validation compares source snapshot against target and ledger without mutating either. It proves:

- source-to-target count parity for every eligible category;
- unique legacy ID map and no duplicate provenance/evidence identity;
- content/reason/version/timestamp/expiry parity subject only to documented redaction or policy transformation;
- preservation of workspace/chat/run/message/execution source references;
- correct exclusions: disabled setting retained as policy, candidates excluded by default, expired/event rules explained;
- all failed rows have a safe error code and replay command;
- re-running the same import changes no counts and reports only `already-imported`.

Parity cannot be asserted by raw content in stdout or telemetry. The report joins opaque IDs with checksums and explicit transformation counters. Any nonzero unexplained difference fails the run.

## Shadow, cutover, rollback and deletion boundary

After validated import, Phase 5 executes equivalent LanceDB retrieval in shadow while SQLite remains the serving source and prompts remain unchanged. Reports compare result IDs, evidence IDs, context token cost, duplicate rate and p50/p95 latency under the same authorised selector/config version. Synthetic corpus reports are coverage evidence only; they are not a production baseline.

Phase 6 enables reads by tenant/project/workspace flag only after the approved quality/SLO gate. The technical default flag is `off`; rollback is a configuration change back to SQLite fallback. Empty retrieval returns the bounded, cited single-summary fallback when eligible — not a fabricated minimum candidate set.

Phase 7 replaces direct observer SQL writes with idempotent command/outbox ingestion. Migration tooling remains separate and does not become a long-lived live dual-writer. Phase 8 happens only after the approved rollback/retention window: disable legacy writes, preserve read-only tooling for the agreed period, export final audit manifest, then remove legacy tables/package references. No migration command deletes AGM rows or performs destructive rollback automatically.

## Operational controls

| Risk | Control and evidence |
| --- | --- |
| Partial import or retry duplication | atomic ledger + record transaction, resumable manifest, idempotency test |
| Scope leak from bad mapping | P0-01 mapping validation, negative selector tests, scope in every target index/query |
| Redaction changes content | common ingress redactor, transformation counters and redaction-versioned parity report |
| Silent data loss | per-category counts/checksums, explicit skipped reason, validation-only gate |
| Expiry accidentally extended | target preserves legacy absolute expiry; zero eligible expired hot records returned |
| Incompatible embedding/index | schema + embedding versions in manifest, adapter rejects/partitions incompatible queries |
| Cutover regressions | shadow reports, feature flag, bounded fallback, configuration-only rollback |

## Handoff to later owners

| Owner | Needed artifact from this runbook | Completion signal |
| --- | --- | --- |
| contracts/core | scope/provenance/idempotency invariants | import fixtures compile against public contracts |
| storage | target schema/index fields and atomic batch expectation | reopen/batch/delete/filter suite passes |
| migration | CLI modes, mapping table, manifest schema | dry-run, resume and validate-only tested |
| analyst/quality | manifest references and shadow comparison inputs | report distinguishes synthetic from measured data |
| cutover | flag/fallback/rollback constraints | canary criteria evaluated with persisted report |
| decommission | final audit and archive requirements | no destructive action before approved window |
