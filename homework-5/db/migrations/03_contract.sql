-- =====================================================================================
--  CAMPUS-110 · ФАЗА 3 — CONTRACT (удаление старой структуры)      релиз v1.10.0
--
--  ВЫПОЛНЯЕТСЯ НЕ В v1.9.0, а отдельным релизом, только когда:
--    ✔ все поды API работают на v1.9+ и флаг Migration:WriteLegacyTimeSlot = false;
--    ✔ мобильное приложение «Умный кампус» ≥ 3.4 и турникеты СК читают start/end, а не timeSlot;
--    ✔ 14 дней в логах/трейсах нет запросов с campus.request.format = legacy_time_slot;
--    ✔ 02_migrate.sql завершён, ограничение bookings_time_range_not_null — VALID.
--  ⚠ ТОЧКА НЕВОЗВРАТА: после DROP COLUMN откат только восстановлением из бэкапа (PITR).
--    Перед запуском: pg_dump -t sports.bookings > bookings_before_contract.sql
-- =====================================================================================

SET lock_timeout = '3s';
SET statement_timeout = '60s';

-- 0. Предусловия — если что-то не так, скрипт останавливается и ничего не удаляет
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.schema_migrations WHERE version = '110_02_migrate') THEN
        RAISE EXCEPTION 'CAMPUS-110: phase 2 (02_migrate.sql) has not been applied';
    END IF;
    IF EXISTS (SELECT 1 FROM sports.bookings WHERE time_range IS NULL) THEN
        RAISE EXCEPTION 'CAMPUS-110: there are rows without time_range';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_constraint
               WHERE conname = 'bookings_time_range_not_null' AND NOT convalidated) THEN
        RAISE EXCEPTION 'CAMPUS-110: constraint bookings_time_range_not_null is not validated';
    END IF;
END;
$$;

BEGIN;

-- 1. Синхронизация больше не нужна — старых клиентов нет
DROP TRIGGER  IF EXISTS trg_bookings_sync_time_slot ON sports.bookings;
DROP FUNCTION IF EXISTS sports.bookings_sync_time_slot();

-- 2. NOT NULL — без сканирования таблицы, т.к. есть валидный CHECK (time_range IS NOT NULL)
ALTER TABLE sports.bookings ALTER COLUMN time_range SET NOT NULL;
ALTER TABLE sports.bookings DROP CONSTRAINT bookings_time_range_not_null;

-- 3. Удаление старой колонки (только каталог; индекс ix_bookings_facility_slot удаляется вместе с ней)
ALTER TABLE sports.bookings DROP COLUMN time_slot;

-- 4. Служебные объекты миграции
DROP PROCEDURE IF EXISTS sports.backfill_time_range(integer, numeric);
DROP FUNCTION  IF EXISTS sports.format_legacy_time_slot(tstzrange);
DROP FUNCTION  IF EXISTS sports.parse_legacy_time_slot(text);

-- 5. Ради чего всё затевалось: защита от двойного бронирования на уровне БД.
--    Две активные брони одной площадки не могут пересекаться по времени.
--    Таблица ~50 тыс. строк — построение индекса < 1 с, выполняется в окне обслуживания.
ALTER TABLE sports.bookings
    ADD CONSTRAINT bookings_no_overlap
    EXCLUDE USING gist (facility_id WITH =, time_range WITH &&)
    WHERE (status = 'Active');

INSERT INTO public.schema_migrations (version, jira_issue, release)
VALUES ('110_03_contract', 'CAMPUS-110', 'v1.10.0')
ON CONFLICT (version) DO NOTHING;

COMMIT;

-- 6. Индекс фазы 1 дублирует индекс ограничения bookings_no_overlap
DROP INDEX CONCURRENTLY IF EXISTS sports.ix_bookings_facility_time_range;

-- Журнал ошибок миграции оставляем на 30 дней для аудита, затем:
-- DROP TABLE sports.bookings_migration_errors;
