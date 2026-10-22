-- =====================================================================================
--  CAMPUS-110 · ФАЗА 2 — MIGRATE (backfill исторических данных)        релиз v1.9.0
--
--  Что делает: переносит существующие брони из time_slot (string) в time_range (tstzrange).
--  Как:        пакетами по 5 000 строк, каждый пакет — отдельная короткая транзакция
--              (COMMIT внутри процедуры) + пауза, чтобы не создавать нагрузку на реплики
--              и не держать блокировки строк дольше миллисекунд.
--              FOR UPDATE SKIP LOCKED — не ждём строки, которые сейчас меняет приложение.
--  Идемпотентность: обрабатываются только строки с time_range IS NULL — скрипт можно
--              прерывать и перезапускать сколько угодно раз.
--  Запуск:     psql -v ON_ERROR_STOP=1 -f 02_migrate.sql   (вне транзакции — из-за COMMIT в процедуре)
-- =====================================================================================

SET lock_timeout = '3s';

CREATE OR REPLACE PROCEDURE sports.backfill_time_range(
    p_batch_size integer DEFAULT 5000,
    p_pause_sec  numeric DEFAULT 0.1)
LANGUAGE plpgsql
AS $$
DECLARE
    v_ok      bigint;
    v_bad     bigint;
    v_total   bigint := 0;
    v_errors  bigint := 0;
    v_batch   integer := 0;
BEGIN
    LOOP
        WITH batch AS (
            SELECT b.id, b.time_slot
            FROM sports.bookings b
            WHERE b.time_range IS NULL
              AND NOT EXISTS (SELECT 1 FROM sports.bookings_migration_errors e
                              WHERE e.booking_id = b.id AND e.resolved_at IS NULL)
            ORDER BY b.id
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED
        ),
        parsed AS (
            SELECT id, time_slot, sports.parse_legacy_time_slot(time_slot) AS r
            FROM batch
        ),
        good AS (
            UPDATE sports.bookings b
            SET    time_range = p.r
            FROM   parsed p
            WHERE  b.id = p.id AND p.r IS NOT NULL
            RETURNING b.id
        ),
        bad AS (
            INSERT INTO sports.bookings_migration_errors (booking_id, time_slot, reason)
            SELECT id, time_slot, 'unparseable time_slot'
            FROM parsed WHERE r IS NULL
            ON CONFLICT (booking_id) DO UPDATE
                SET logged_at = now(), resolved_at = NULL, time_slot = EXCLUDED.time_slot
            RETURNING booking_id
        )
        SELECT (SELECT count(*) FROM good), (SELECT count(*) FROM bad) INTO v_ok, v_bad;

        EXIT WHEN v_ok + v_bad = 0;

        v_batch  := v_batch + 1;
        v_total  := v_total + v_ok;
        v_errors := v_errors + v_bad;
        COMMIT;                                   -- короткая транзакция на каждый пакет

        RAISE NOTICE 'batch %: migrated %, errors % (total migrated %)', v_batch, v_ok, v_bad, v_total;
        PERFORM pg_sleep(p_pause_sec);
    END LOOP;

    RAISE NOTICE 'Backfill finished: % rows migrated, % rows logged to sports.bookings_migration_errors',
        v_total, v_errors;
END;
$$;

-- 1. Перенос данных
CALL sports.backfill_time_range(5000, 0.1);

-- 2. Контроль полноты и корректности переноса
DO $$
DECLARE
    v_null     bigint;
    v_errors   bigint;
    v_mismatch bigint;
BEGIN
    SELECT count(*) INTO v_null   FROM sports.bookings WHERE time_range IS NULL;
    SELECT count(*) INTO v_errors FROM sports.bookings_migration_errors WHERE resolved_at IS NULL;

    -- обратное преобразование должно давать ту же строку, что и разбор исходной
    SELECT count(*) INTO v_mismatch
    FROM sports.bookings
    WHERE time_range IS DISTINCT FROM sports.parse_legacy_time_slot(time_slot);

    RAISE NOTICE 'Verification: time_range IS NULL = %, unresolved errors = %, mismatches = %',
        v_null, v_errors, v_mismatch;

    IF v_null > 0 OR v_errors > 0 OR v_mismatch > 0 THEN
        RAISE EXCEPTION 'CAMPUS-110: backfill incomplete — see sports.bookings_migration_errors. '
                        'Fix rows manually (UPDATE ... SET time_range = ...), mark resolved_at and re-run 02_migrate.sql';
    END IF;
END;
$$;

-- 3. Подготовка к фазе 3: ограничение NOT NULL в виде CHECK.
--    ADD ... NOT VALID — мгновенно; VALIDATE — сканирует таблицу под блокировкой
--    SHARE UPDATE EXCLUSIVE, которая НЕ мешает INSERT/UPDATE/DELETE.
--    Благодаря валидному CHECK в 03_contract.sql команда SET NOT NULL выполнится без полного сканирования.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
                   WHERE conrelid = 'sports.bookings'::regclass
                     AND conname = 'bookings_time_range_not_null') THEN
        ALTER TABLE sports.bookings
            ADD CONSTRAINT bookings_time_range_not_null CHECK (time_range IS NOT NULL) NOT VALID;
    END IF;
END;
$$;
ALTER TABLE sports.bookings VALIDATE CONSTRAINT bookings_time_range_not_null;
ALTER TABLE sports.bookings VALIDATE CONSTRAINT bookings_time_range_valid;

ANALYZE sports.bookings;

INSERT INTO public.schema_migrations (version, jira_issue, release)
VALUES ('110_02_migrate', 'CAMPUS-110', 'v1.9.0')
ON CONFLICT (version) DO NOTHING;
