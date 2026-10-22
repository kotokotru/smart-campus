namespace Campus.Sports.Api.Domain;

public enum BookingStatus
{
    Active,
    Cancelled
}

/// <summary>Бронь площадки спортивного комплекса (зал, бассейн, корт, тренажёрный зал).</summary>
public sealed record SportsBooking(
    Guid Id,
    string FacilityId,
    string StudentId,
    TimeRange TimeRange,
    BookingStatus Status,
    bool IsSanityTest,
    DateTimeOffset CreatedAt)
{
    /// <summary>Старое строковое представление — пишется в колонку time_slot в фазе Expand.</summary>
    public string LegacyTimeSlot => TimeRange.ToLegacyString();
}

/// <summary>Слот уже занят другой активной бронью на этой площадке.</summary>
public sealed class BookingConflictException(string facilityId, TimeRange range)
    : Exception($"Площадка '{facilityId}' уже забронирована на интервал {range}.");
