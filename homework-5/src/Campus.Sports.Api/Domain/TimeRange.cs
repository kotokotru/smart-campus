using System.Globalization;
using System.Text.RegularExpressions;

namespace Campus.Sports.Api.Domain;

/// <summary>
/// Новый тип интервала бронирования (CAMPUS-110).
/// Заменяет строку TimeSlot вида "2026-11-05 18:00-19:30".
/// Полуоткрытый интервал [Start, End) — так же, как tstzrange '[)' в PostgreSQL,
/// поэтому соседние слоты 18:00-19:30 и 19:30-21:00 не пересекаются.
/// </summary>
public readonly record struct TimeRange
{
    /// <summary>Часовой пояс кампуса (Санкт-Петербург, UTC+3, без перехода на летнее время).</summary>
    public static readonly TimeSpan CampusOffset = TimeSpan.FromHours(3);

    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(4);

    private static readonly Regex LegacyPattern = new(
        @"^\s*(?<date>\d{4}-\d{2}-\d{2})\s+(?<from>\d{2}:\d{2})\s*-\s*(?<to>\d{2}:\d{2})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }

    public TimeRange(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
            throw new ArgumentException("Окончание слота должно быть позже начала.", nameof(end));

        var duration = end - start;
        if (duration < MinDuration || duration > MaxDuration)
            throw new ArgumentException(
                $"Длительность слота должна быть от {MinDuration.TotalMinutes} мин до {MaxDuration.TotalHours} ч.",
                nameof(end));

        Start = start.ToUniversalTime();
        End = end.ToUniversalTime();
    }

    public TimeSpan Duration => End - Start;

    /// <summary>Пересечение полуоткрытых интервалов (аналог оператора &amp;&amp; для tstzrange).</summary>
    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;

    /// <summary>
    /// Разбор старого строкового формата TimeSlot ("yyyy-MM-dd HH:mm-HH:mm", местное время кампуса).
    /// Та же логика повторена в SQL-функции sports.parse_legacy_time_slot (01_expand.sql).
    /// </summary>
    public static TimeRange ParseLegacy(string timeSlot)
    {
        if (!TryParseLegacy(timeSlot, out var range))
            throw new FormatException($"Некорректный TimeSlot: '{timeSlot}'. Ожидается 'yyyy-MM-dd HH:mm-HH:mm'.");
        return range;
    }

    public static bool TryParseLegacy(string? timeSlot, out TimeRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(timeSlot)) return false;

        var m = LegacyPattern.Match(timeSlot);
        if (!m.Success) return false;

        if (!DateTime.TryParseExact(m.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) return false;
        if (!TimeSpan.TryParseExact(m.Groups["from"].Value, @"hh\:mm", CultureInfo.InvariantCulture, out var from)) return false;
        if (!TimeSpan.TryParseExact(m.Groups["to"].Value, @"hh\:mm", CultureInfo.InvariantCulture, out var to)) return false;

        var start = new DateTimeOffset(date + from, CampusOffset);
        var end = new DateTimeOffset(date + to, CampusOffset);
        if (end <= start) return false;                 // слоты «через полночь» в старом формате не поддерживались

        try { range = new TimeRange(start, end); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Обратное преобразование в старый формат — нужно для dual-write в фазе Expand,
    /// пока клиенты v1.8 (мобильное приложение, турникеты СК) читают колонку time_slot.
    /// </summary>
    public string ToLegacyString()
    {
        var s = Start.ToOffset(CampusOffset);
        var e = End.ToOffset(CampusOffset);
        return string.Create(CultureInfo.InvariantCulture, $"{s:yyyy-MM-dd} {s:HH:mm}-{e:HH:mm}");
    }

    public override string ToString() => $"[{Start:O}, {End:O})";
}
