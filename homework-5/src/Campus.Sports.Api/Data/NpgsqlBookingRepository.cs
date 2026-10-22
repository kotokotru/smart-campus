using Campus.Sports.Api.Domain;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Campus.Sports.Api.Data;

/// <summary>
/// Репозиторий броней на PostgreSQL.
/// Фаза 1 (Expand) паттерна Expand and Contract: DUAL-WRITE —
/// каждая запись сохраняется одновременно в новую колонку time_range (tstzrange)
/// и в старую колонку time_slot (varchar), чтобы клиенты v1.8 продолжали работать.
/// Чтение — из time_range, с fallback на разбор time_slot для ещё не мигрированных строк.
/// </summary>
public sealed class NpgsqlBookingRepository(
    NpgsqlDataSource dataSource,
    IOptionsMonitor<MigrationOptions> migration,
    ILogger<NpgsqlBookingRepository> logger) : IBookingRepository
{
    private const string SelectColumns =
        "id, facility_id, student_id, time_range, time_slot, status, is_sanity_test, created_at";

    public async Task<SportsBooking?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {SelectColumns} FROM sports.bookings WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task CreateAsync(SportsBooking booking, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Сериализуем создание броней по одной площадке, чтобы не получить двойную бронь
        // (до фазы Contract, где появится EXCLUDE-ограничение на уровне БД).
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@f))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("f", booking.FacilityId);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var overlap = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM sports.bookings
                WHERE facility_id = @f
                  AND status = 'Active'
                  AND time_range && @r)
            """, conn, tx))
        {
            overlap.Parameters.AddWithValue("f", booking.FacilityId);
            overlap.Parameters.Add(RangeParameter("r", booking.TimeRange));
            if ((bool)(await overlap.ExecuteScalarAsync(ct))!)
                throw new BookingConflictException(booking.FacilityId, booking.TimeRange);
        }

        // ---- DUAL-WRITE (Expand) ------------------------------------------------------
        var writeLegacy = migration.CurrentValue.WriteLegacyTimeSlot;

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO sports.bookings
                (id, facility_id, student_id, time_range, time_slot, status, is_sanity_test, created_at)
            VALUES
                (@id, @f, @s, @r, @slot, @status, @sanity, @created)
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", booking.Id);
            insert.Parameters.AddWithValue("f", booking.FacilityId);
            insert.Parameters.AddWithValue("s", booking.StudentId);
            insert.Parameters.Add(RangeParameter("r", booking.TimeRange));                 // НОВАЯ структура
            insert.Parameters.Add(new NpgsqlParameter("slot", NpgsqlDbType.Varchar)        // СТАРАЯ структура
            {
                Value = writeLegacy ? booking.LegacyTimeSlot : DBNull.Value
            });
            insert.Parameters.AddWithValue("status", booking.Status.ToString());
            insert.Parameters.AddWithValue("sanity", booking.IsSanityTest);
            insert.Parameters.AddWithValue("created", booking.CreatedAt.UtcDateTime);
            await insert.ExecuteNonQueryAsync(ct);
        }
        // ---------------------------------------------------------------------------------

        await tx.CommitAsync(ct);
        logger.LogInformation("Booking {BookingId} created (dual-write legacy={Legacy})", booking.Id, writeLegacy);
    }

    public async Task<bool> CancelOrPurgeAsync(Guid id, CancellationToken ct)
    {
        // Тестовые брони sanity-теста удаляются физически (Cleanup), реальные — отменяются.
        await using var purge = dataSource.CreateCommand(
            "DELETE FROM sports.bookings WHERE id = @id AND is_sanity_test");
        purge.Parameters.AddWithValue("id", id);
        if (await purge.ExecuteNonQueryAsync(ct) > 0) return true;

        await using var cancel = dataSource.CreateCommand(
            "UPDATE sports.bookings SET status = 'Cancelled' WHERE id = @id AND status = 'Active'");
        cancel.Parameters.AddWithValue("id", id);
        return await cancel.ExecuteNonQueryAsync(ct) > 0;
    }

    private static NpgsqlParameter RangeParameter(string name, TimeRange range) =>
        new(name, NpgsqlDbType.TimestampTzRange)
        {
            // '[)' — начало включено, конец исключён
            Value = new NpgsqlRange<DateTime>(range.Start.UtcDateTime, true, range.End.UtcDateTime, false)
        };

    private static SportsBooking Map(NpgsqlDataReader r)
    {
        TimeRange range;
        if (!r.IsDBNull(3))
        {
            var pg = r.GetFieldValue<NpgsqlRange<DateTime>>(3);
            range = new TimeRange(
                new DateTimeOffset(DateTime.SpecifyKind(pg.LowerBound, DateTimeKind.Utc)),
                new DateTimeOffset(DateTime.SpecifyKind(pg.UpperBound, DateTimeKind.Utc)));
        }
        else
        {
            // Строка ещё не обработана 02_migrate.sql — читаем старый формат
            range = TimeRange.ParseLegacy(r.GetString(4));
        }

        return new SportsBooking(
            Id: r.GetGuid(0),
            FacilityId: r.GetString(1),
            StudentId: r.GetString(2),
            TimeRange: range,
            Status: Enum.Parse<BookingStatus>(r.GetString(5)),
            IsSanityTest: r.GetBoolean(6),
            CreatedAt: new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc)));
    }
}
