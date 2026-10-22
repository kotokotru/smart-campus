-- =====================================================================================
--  ИС «Умный кампус» · модуль «Спортивный комплекс»
--  00_baseline_v1.8.sql — исходная схема (версия 1.8.x) + демо-данные для стенда.
--  В production НЕ выполняется: нужен только чтобы воспроизвести миграцию CAMPUS-110 локально.
-- =====================================================================================

CREATE SCHEMA IF NOT EXISTS sports;

CREATE TABLE IF NOT EXISTS sports.bookings
(
    id             uuid         PRIMARY KEY,
    facility_id    text         NOT NULL,                   -- gym-hall-1, pool-lane-3, tennis-court-2 ...
    student_id     text         NOT NULL,
    time_slot      varchar(32)  NOT NULL,                   -- СТАРЫЙ формат: '2026-11-05 18:00-19:30' (МСК)
    status         text         NOT NULL DEFAULT 'Active'
                                CHECK (status IN ('Active', 'Cancelled')),
    is_sanity_test boolean      NOT NULL DEFAULT false,
    created_at     timestamptz  NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_bookings_facility_slot ON sports.bookings (facility_id, time_slot);

-- ---------- демо-данные: ~46 000 исторических броней за 2 года ----------------------
INSERT INTO sports.bookings (id, facility_id, student_id, time_slot, status, created_at)
SELECT gen_random_uuid(),
       (ARRAY['gym-hall-1','gym-hall-2','pool-lane-1','pool-lane-2','pool-lane-3',
              'tennis-court-1','tennis-court-2','fitness-room'])[1 + (g % 8)],
       'st-' || lpad((g % 4200)::text, 5, '0'),
       to_char(d, 'YYYY-MM-DD') || ' ' ||
       to_char(time '08:00' + make_interval(mins => 90 * h), 'HH24:MI') || '-' ||
       to_char(time '08:00' + make_interval(mins => 90 * h + 90), 'HH24:MI'),
       CASE WHEN g % 11 = 0 THEN 'Cancelled' ELSE 'Active' END,
       d - interval '3 days'
FROM generate_series(1, 46000) AS g
CROSS JOIN LATERAL (SELECT date '2024-11-01' + ((g / 64) % 730) AS d, (g / 8) % 8 AS h) s
ON CONFLICT DO NOTHING;

-- Несколько строк в «альтернативном» формате, который когда-то вводила админ-панель деканата.
INSERT INTO sports.bookings (id, facility_id, student_id, time_slot, status) VALUES
    (gen_random_uuid(), 'pool-lane-2',   'st-00042', '05.09.2025 10:00-11:00', 'Cancelled'),
    (gen_random_uuid(), 'gym-hall-2',    'st-00777', '12.12.2025 19:30 - 21:00', 'Cancelled');
