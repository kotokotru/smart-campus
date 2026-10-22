-- =====================================================================================
--  CAMPUS-110 · Спортивный комплекс · TimeSlot (string) → TimeRange (tstzrange)
--  ФАЗА 1 — EXPAND                                         релиз v1.9.0 (CR-2026-110)
--
--  Что делает: добавляет НОВУЮ структуру рядом со старой, ничего не удаляя.
--  Совместимость:  приложение v1.8 (пишет только time_slot)  — работает;
--                  приложение v1.9 (dual-write в обе колонки) — работает.
--  Блокировки:     ADD COLUMN без DEFAULT — только изменение каталога (миллисекунды);
--                  индекс строится CONCURRENTLY — запись в таблицу не блокируется.
--  Запуск:         psql -v ON_ERROR_STOP=1 -f 01_expand.sql   (НЕ внутри транзакции:
--                  CREATE INDEX CONCURRENTLY нельзя выполнять в блоке BEGIN/COMMIT)
--  Откат:          01_expand_rollback.sql
-- =====================================================================================

-- Не ждать бесконечно, если таблицу держит долгая транзакция: лучше упасть и повторить.
SET lock_timeout = '3s';
SET statement_timeout = '60s';

CREATE EXTENSION IF NOT EXISTS btree_gist;   -- GiST по (text, range) — для поиска пересечений слотов

BEGIN;

-- 1. Новая колонка. NULL допустим, пока не выполнена фаза 2 (backfill).
ALTER TABLE sports.bookings
    ADD COLUMN IF NOT EXISTS time_range tstzrange;

COMMENT ON COLUMN sports.bookings.time_range IS
    'CAMPUS-110: интервал брони [start, end) в UTC. Заменяет time_slot.';
COMMENT ON COLUMN sports.bookings.time_slot IS
    'DEPRECATED (CAMPUS-110): строка "YYYY-MM-DD HH:MI-HH:MI" (МСК). Удаляется в 03_contract.sql (v1.10.0).';

-- 2. Корректность нового поля. NOT VALID — старые строки не проверяются (нет полного сканирования),
--    но все НОВЫЕ вставки/обновления проверяются сразу. VALIDATE выполняется в 02_migrate.sql.
ALTER TABLE sports.bookings
    ADD CONSTRAINT bookings_time_range_valid CHECK (
        time_range IS NULL OR (
            NOT isempty(time_range)
            AND NOT lower_inf(time_range) AND NOT upper_inf(time_range)
            AND lower_inc(time_range) AND NOT upper_inc(time_range)           -- строго '[)'
            AND upper(time_range) - lower(time_range) BETWEEN interval '15 minutes' AND interval '4 hours'
        )
    ) NOT VALID;

-- 3. Функции преобразования форматов (одна логика с TimeRange.ParseLegacy в C#).
CREATE OR REPLACE FUNCTION sports.parse_legacy_time_slot(p_slot text)
RETURNS tstzrange
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    m       text[];
    v_date  date;
    v_start timestamptz;
    v_end   timestamptz;
BEGIN
    -- основной формат API: 2026-11-05 18:00-19:30
    m := regexp_match(p_slot, '^\s*(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2})\s*-\s*(\d{2}:\d{2})\s*$');
    IF m IS NOT NULL THEN
        v_date := to_date(m[1], 'YYYY-MM-DD');
    ELSE
        -- исторический формат админ-панели деканата: 05.09.2025 10:00-11:00
        m := regexp_match(p_slot, '^\s*(\d{2}\.\d{2}\.\d{4})\s+(\d{2}:\d{2})\s*-\s*(\d{2}:\d{2})\s*$');
        IF m IS NULL THEN
            RETURN NULL;
        END IF;
        v_date := to_date(m[1], 'DD.MM.YYYY');
    END IF;

    -- время в старом поле хранилось как местное время кампуса (Europe/Moscow)
    v_start := (v_date + m[2]::time) AT TIME ZONE 'Europe/Moscow';
    v_end   := (v_date + m[3]::time) AT TIME ZONE 'Europe/Moscow';

    IF v_end <= v_start THEN
        RETURN NULL;
    END IF;
    RETURN tstzrange(v_start, v_end, '[)');
EXCEPTION
    WHEN invalid_datetime_format OR datetime_field_overflow THEN
        RETURN NULL;                     -- некорректная дата/время → строка уйдёт в журнал ошибок
END;
$$;

CREATE OR REPLACE FUNCTION sports.format_legacy_time_slot(p_range tstzrange)
RETURNS varchar
LANGUAGE sql STABLE
AS $$
    SELECT to_char(lower(p_range) AT TIME ZONE 'Europe/Moscow', 'YYYY-MM-DD HH24:MI') || '-' ||
           to_char(upper(p_range) AT TIME ZONE 'Europe/Moscow', 'HH24:MI');
$$;

-- 4. Триггер-синхронизатор на время rolling update.
--    Во время выката одновременно работают поды v1.8 (пишут только time_slot)
--    и поды v1.9 (dual-write). Триггер гарантирует, что обе колонки согласованы
--    независимо от того, какой под обработал запрос.
CREATE OR REPLACE FUNCTION sports.bookings_sync_time_slot()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.time_range IS NULL AND NEW.time_slot IS NOT NULL THEN
            NEW.time_range := sports.parse_legacy_time_slot(NEW.time_slot);   -- запись от v1.8
        ELSIF NEW.time_slot IS NULL AND NEW.time_range IS NOT NULL THEN
            NEW.time_slot := sports.format_legacy_time_slot(NEW.time_range);  -- запись от v1.10 (флаг выключен)
        END IF;

    ELSIF TG_OP = 'UPDATE' THEN
        IF NEW.time_slot IS DISTINCT FROM OLD.time_slot
           AND NEW.time_range IS NOT DISTINCT FROM OLD.time_range THEN
            -- клиент v1.8 перенёс бронь, изменив только строку
            NEW.time_range := sports.parse_legacy_time_slot(NEW.time_slot);
        ELSIF NEW.time_range IS DISTINCT FROM OLD.time_range
              AND NEW.time_slot IS NOT DISTINCT FROM OLD.time_slot
              AND sports.parse_legacy_time_slot(NEW.time_slot) IS DISTINCT FROM NEW.time_range THEN
            -- клиент v1.9 перенёс бронь, изменив только интервал
            NEW.time_slot := sports.format_legacy_time_slot(NEW.time_range);
        END IF;
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_bookings_sync_time_slot ON sports.bookings;
CREATE TRIGGER trg_bookings_sync_time_slot
    BEFORE INSERT OR UPDATE OF time_slot, time_range ON sports.bookings
    FOR EACH ROW EXECUTE FUNCTION sports.bookings_sync_time_slot();

-- 5. Журнал строк, которые не удалось разобрать при переносе (заполняется в фазе 2).
CREATE TABLE IF NOT EXISTS sports.bookings_migration_errors
(
    booking_id  uuid        PRIMARY KEY,
    time_slot   text,
    reason      text        NOT NULL,
    logged_at   timestamptz NOT NULL DEFAULT now(),
    resolved_at timestamptz
);

-- 6. Отметка о применённой миграции (цифровой след: Jira ↔ релиз ↔ БД)
CREATE TABLE IF NOT EXISTS public.schema_migrations
(
    version     text        PRIMARY KEY,
    jira_issue  text        NOT NULL,
    release     text        NOT NULL,
    applied_at  timestamptz NOT NULL DEFAULT now()
);
INSERT INTO public.schema_migrations (version, jira_issue, release)
VALUES ('110_01_expand', 'CAMPUS-110', 'v1.9.0')
ON CONFLICT (version) DO NOTHING;

COMMIT;

-- 7. Индекс для поиска пересечений слотов по площадке — без блокировки записи.
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_bookings_facility_time_range
    ON sports.bookings USING gist (facility_id, time_range)
    WHERE status = 'Active';
