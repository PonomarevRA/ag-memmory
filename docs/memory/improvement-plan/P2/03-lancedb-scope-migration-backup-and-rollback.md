# P2.3. Миграция LanceDB, backup и rollback

**Владелец:** Storage + release owner
**Зависимости:** P2.2
**Статус:** conditional

## Цель

Безопасно перенести scope-bearing данные в новую схему.

## Сделать

- Выполнить copy/export в отдельное хранилище, freeze исходного набора и transform каждого record/catalog/relation.
- Провалидировать counts, hashes, selectors, reader routes и ссылки до cutover marker.
- Сохранить оригинал неизменным; описать переключение назад и доказать rollback на копии.

## Приёмка

Есть воспроизводимый dry-run, validation report, cutover marker и проверенный restore без разрушения исходной базы.
