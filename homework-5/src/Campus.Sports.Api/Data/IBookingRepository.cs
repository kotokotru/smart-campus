using Campus.Sports.Api.Domain;

namespace Campus.Sports.Api.Data;

public interface IBookingRepository
{
    Task<SportsBooking?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Создаёт бронь. Бросает <see cref="BookingConflictException"/>, если слот пересекается с активной бронью.</summary>
    Task CreateAsync(SportsBooking booking, CancellationToken ct);

    /// <summary>Отмена брони (soft delete). Тестовые sanity-брони удаляются физически.</summary>
    Task<bool> CancelOrPurgeAsync(Guid id, CancellationToken ct);
}

/// <summary>
/// Флаги миграции CAMPUS-110 (секция "Migration" в appsettings / ConfigMap).
/// Позволяют переключать поведение без нового деплоя.
/// </summary>
public sealed class MigrationOptions
{
    public const string Section = "Migration";

    /// <summary>
    /// Фаза Expand: писать одновременно в новую колонку time_range И в старую time_slot.
    /// Выключается в v1.10.0 перед выполнением 03_contract.sql.
    /// </summary>
    public bool WriteLegacyTimeSlot { get; set; } = true;
}
