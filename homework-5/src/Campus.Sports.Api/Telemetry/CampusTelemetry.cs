using System.Diagnostics;

namespace Campus.Sports.Api.Telemetry;

/// <summary>
/// Единая точка цифрового следа (CALS / Digital Traceability) сервиса «Спортивный комплекс».
/// Связывает span OpenTelemetry с задачей Jira и с доменной сущностью,
/// чтобы по trace_id можно было найти задачу, коммит, релиз и конкретную бронь.
/// </summary>
public static class CampusTelemetry
{
    /// <summary>Имя ActivitySource — его нужно зарегистрировать в AddOpenTelemetry().AddSource(...).</summary>
    public const string SourceName = "Campus.Sports";

    /// <summary>Задача Jira варианта 10: изменение формата TimeSlot (string → TimeRange).</summary>
    public const string JiraIssue = "CAMPUS-110";

    public const string ServiceVersion = "1.9.0";

    // Имена тегов (семантика «Умного кампуса»)
    public const string TagJiraIssue      = "jira.issue";
    public const string TagBookingId      = "campus.booking_id";
    public const string TagFacilityId     = "campus.facility_id";
    public const string TagTimeRangeStart = "campus.time_range.start";
    public const string TagTimeRangeEnd   = "campus.time_range.end";
    public const string TagSchemaPhase    = "campus.db.migration_phase";
    public const string TagSanity         = "campus.sanity_test";

    public static readonly ActivitySource Source = new(SourceName, ServiceVersion);

    /// <summary>
    /// Пробрасывает теги в текущий span (Activity.Current, созданный ASP.NET Core instrumentation)
    /// и, если передан, — в дочерний доменный span.
    /// </summary>
    public static void Enrich(Activity? activity, string? bookingId = null, string? facilityId = null)
    {
        if (activity is null) return;          // трассировка выключена или нет слушателя — ничего не делаем

        activity.SetTag(TagJiraIssue, JiraIssue);
        activity.SetTag(TagSchemaPhase, "expand");   // v1.9.0 работает в фазе Expand (dual-write)
        if (bookingId is not null)  activity.SetTag(TagBookingId, bookingId);
        if (facilityId is not null) activity.SetTag(TagFacilityId, facilityId);
    }
}
