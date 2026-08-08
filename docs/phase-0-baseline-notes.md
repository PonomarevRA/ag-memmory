# Фаза 0 — baseline и design lock

> Владелец: `phase0_baseline`  
> Статус: `in progress` — фактический baseline готов, решения design lock ожидают владельца продукта/платформы.

## Summary

Созданы доказуемые документы о текущем состоянии AGM и versioned corpus без доступа к production data. Фаза не закрыта, потому что пять обязательных решений из exit criteria плана нельзя безопасно вывести из кода.

## Выполнено

- Зафиксирована схема, flows, ограничители, redaction, API, scope, retention и deletion responsibilities: [`current-state-agm.md`](current-state-agm.md).
- Создан детерминированный quality corpus: [`quality-corpus/agm-memory-stage-zero-v1.json`](quality-corpus/agm-memory-stage-zero-v1.json). Он получен исключительно из `GoldenCorpusFixture.V1` unit test, поэтому не содержит production/пользовательских данных.
- Проверен исходный модуль и его production integration на ревизии `a704d90`; исходники не изменялись.

## Что именно означает corpus

Это JSON-проекция шести тестовых slices (`ExactIdentifier`, `SemanticParaphrase`, `ShortFollowUp`, `Freshness`, `History`, `Conflict`) с expected memory/source IDs. Он подходит для проверки совместимости формата и evaluator-а после переноса, но **не** подтверждает реальную relevance или latency: текущий evaluator принимает готовые ranked hits и не вызывает storage query.

## Безопасный путь к следующей анонимизированной fixture

1. В отдельной approved export-команде читать только разрешённый AGM SQLite snapshot или export API, никогда не live database.
2. До записи fixture заменить workspace/chat/run/message/execution/legacy IDs на стабильные synthetic aliases; удалить raw content, авторов, пути и credential-like поля.
3. Оставить только query text, slice, relevant synthetic memory IDs, synthetic evidence IDs, scope alias и минимальные метаданные для parity.
4. Прогнать secret scanner/redactor, ручную privacy-review и сохранить manifest с row counts/checksum/version. Публиковать fixture только после approval retention/authorization owner.

Текущая фаза намеренно этого не делает: права на production data, retention policy и формальный export contract ещё не утверждены.

## Критерий готовности и передача

Необходимые решения и их последствия перечислены в [`phase-0-open-problems.md`](phase-0-open-problems.md). После их фиксации следующий исполнитель должен обновить статус фазы 0 на `complete` и передать Phase 1 точный baseline: [`docs/current-state-agm.md`](current-state-agm.md).
