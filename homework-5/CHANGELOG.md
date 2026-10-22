# Changelog — campus-sports-api

Все значимые изменения модуля «Спортивный комплекс» ИС «Умный кампус».

Формат основан на [Keep a Changelog 1.1.0](https://keepachangelog.com/ru/1.1.0/),
версии соответствуют [Semantic Versioning 2.0.0](https://semver.org/lang/ru/).

## [Unreleased]

### Breaking Changes
- Планируется в **1.10.0** (CAMPUS-110, фаза Contract): удаление поля `timeSlot` из запросов и ответов
  `POST/GET /api/sports/bookings` и колонки `sports.bookings.time_slot`. Клиентам нужно перейти на `start`/`end`.

## [1.9.0] - 2026-10-28

### Added
- Тип `TimeRange` (интервал `[start, end)`) для броней площадок спорткомплекса; колонка
  `sports.bookings.time_range tstzrange` с CHECK-ограничением и GiST-индексом (CAMPUS-110).
- Поля `start` и `end` (ISO 8601 со смещением) в запросе и ответе `POST /api/sports/bookings`.
- Dual-write в `time_slot` и `time_range`, флаг `Migration:WriteLegacyTimeSlot` (CAMPUS-110).
- Триггер `trg_bookings_sync_time_slot` для согласованности колонок во время rolling update.
- Теги OpenTelemetry: `jira.issue`, `campus.booking_id`, `campus.facility_id`,
  `campus.time_range.start/end`, `campus.request.format`.
- Production Sanity-тест `ProductionSanityTests` с обязательной очисткой тестовой брони.
- Журнал ошибок миграции `sports.bookings_migration_errors`.

### Changed
- Пересечение броней проверяется по интервалу `tstzrange &&` под `pg_advisory_xact_lock` по площадке.
- Время брони хранится в UTC, API отдаёт его со смещением `+03:00`.

### Deprecated
- Поле `timeSlot` (строка `"YYYY-MM-DD HH:MM-HH:MM"`) в API и колонка `time_slot` — будут удалены в 1.10.0.

### Fixed
- Двойное бронирование одной площадки при одновременных запросах (CAMPUS-110).
- Сдвиг времени брони на 3 часа в мобильном приложении (строка без часового пояса трактовалась как UTC).
- Брони из старой админ-панели в формате `DD.MM.YYYY` не отображались в мобильном приложении.

### Breaking Changes
- Нет. Релиз обратно совместим: клиенты 1.8.x продолжают работать с полем `timeSlot`.

## [1.8.3] - 2026-09-30

### Fixed
- Таймаут при выгрузке отчёта посещаемости бассейна за семестр (CAMPUS-097).

[Unreleased]: ../../compare/v1.9.0...HEAD
[1.9.0]: ../../compare/v1.8.3...v1.9.0
[1.8.3]: ../../releases/tag/v1.8.3
