-- =====================================================================================
--  CAMPUS-110 · ОТКАТ ФАЗЫ 1 (Expand)        используется в плане отката CR-2026-110
--
--  Выполнять ТОЛЬКО после `kubectl rollout undo` (когда все поды снова v1.8)
--  и только если 03_contract.sql ещё НЕ выполнялся.
--  Данные в time_slot не теряются: старая колонка всё это время заполнялась (dual-write + триггер).
-- =====================================================================================

SET lock_timeout = '3s';

DROP INDEX CONCURRENTLY IF EXISTS sports.ix_bookings_facility_time_range;

BEGIN;
DROP TRIGGER  IF EXISTS trg_bookings_sync_time_slot ON sports.bookings;
DROP FUNCTION IF EXISTS sports.bookings_sync_time_slot();
DROP PROCEDURE IF EXISTS sports.backfill_time_range(integer, numeric);
DROP FUNCTION IF EXISTS sports.format_legacy_time_slot(tstzrange);
DROP FUNCTION IF EXISTS sports.parse_legacy_time_slot(text);
ALTER TABLE sports.bookings DROP CONSTRAINT IF EXISTS bookings_time_range_not_null;
ALTER TABLE sports.bookings DROP CONSTRAINT IF EXISTS bookings_time_range_valid;
ALTER TABLE sports.bookings DROP COLUMN IF EXISTS time_range;
DROP TABLE IF EXISTS sports.bookings_migration_errors;
DELETE FROM public.schema_migrations WHERE jira_issue = 'CAMPUS-110';
COMMIT;
