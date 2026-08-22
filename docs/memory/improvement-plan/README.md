# Подзадачи плана развития памяти

Каждый файл — самостоятельная задача. `P0` снимает продуктовые и security-блокеры, `P1` даёт безопасный результат без breaking-миграции, `P2` выполняется только при положительном ADR из P0.1.

| Приоритет | Задачи | Правило запуска |
| --- | --- | --- |
| P0 | 01–05 | До реализации новых API и UI. |
| P1 | 01–10 | После зависимых P0; не требует `KnowledgeSpaceId`. |
| P2 | 01–05 | Только если P0.1 докажет недостаточность текущего exact scope. |

Не затрагивать `docs/plans/**`: эта папка удаляется отдельным изменением.

## Порядок

1. P0.1–P0.5.
2. P1.1, затем P1.2–P1.9 в соответствии с зависимостями в файлах.
3. P1.10 — общий nonbreaking acceptance.
4. P2.1–P2.5 — только по условию выше.

## Файлы

- [P0](P0/) — решения и ограничения: [01](P0/01-scope-operational-map-and-adr.md), [02](P0/02-record-and-audit-disclosure-policy.md), [03](P0/03-audit-metrics-retention-policy.md), [04](P0/04-about-and-connect-content-recovery.md), [05](P0/05-feature-flags-and-exposure-boundaries.md).
- [P1](P1/) — безопасная реализация на текущем exact scope: задачи [01](P1/01-authorized-area-catalog-and-reader-contract.md)–[10](P1/10-nonbreaking-integration-and-security-regression.md).
- [P2](P2/) — conditional-ветка `KnowledgeSpaceId`: задачи [01](P2/01-knowledgespace-contract-and-authorization.md)–[05](P2/05-production-rollout-and-post-cutover-review.md).
