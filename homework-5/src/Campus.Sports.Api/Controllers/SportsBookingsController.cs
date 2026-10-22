using System.Diagnostics;
using Campus.Sports.Api.Data;
using Campus.Sports.Api.Domain;
using Campus.Sports.Api.Telemetry;
using Microsoft.AspNetCore.Mvc;

namespace Campus.Sports.Api.Controllers;

/// <summary>
/// ИС «Умный кампус» → модуль «Спортивный комплекс».
/// CAMPUS-110: формат слота бронирования меняется со строки TimeSlot на структуру TimeRange.
/// API v1.9.0 принимает оба формата (обратная совместимость) и возвращает оба поля.
/// </summary>
[ApiController]
[Route("api/sports/bookings")]
public sealed class SportsBookingsController(
    IBookingRepository repository,
    TimeProvider clock,
    ILogger<SportsBookingsController> logger) : ControllerBase
{
    public const string SanityHeader = "X-Campus-Sanity";

    /// <summary>Создать бронь площадки.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(BookingResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateBookingRequest request, CancellationToken ct)
    {
        var bookingId = Guid.NewGuid();
        var isSanity = Request.Headers.TryGetValue(SanityHeader, out var h) && h == "true";

        // 1) Проброс цифрового следа в span HTTP-запроса, созданный ASP.NET Core instrumentation
        CampusTelemetry.Enrich(Activity.Current, bookingId.ToString(), request.FacilityId);
        Activity.Current?.SetTag(CampusTelemetry.TagSanity, isSanity);

        // 2) Доменный дочерний span — бизнес-операция «создание брони»
        using var activity = CampusTelemetry.Source.StartActivity("SportsBooking.Create", ActivityKind.Internal);
        CampusTelemetry.Enrich(activity, bookingId.ToString(), request.FacilityId);

        if (!TryResolveRange(request, out var range, out var error))
        {
            activity?.SetStatus(ActivityStatusCode.Error, error);
            return ValidationProblem(detail: error);
        }

        activity?.SetTag(CampusTelemetry.TagTimeRangeStart, range.Start.ToString("O"));
        activity?.SetTag(CampusTelemetry.TagTimeRangeEnd, range.End.ToString("O"));
        activity?.SetTag("campus.request.format", request.TimeSlot is null ? "time_range" : "legacy_time_slot");

        var booking = new SportsBooking(
            bookingId, request.FacilityId, request.StudentId, range,
            BookingStatus.Active, isSanity, clock.GetUtcNow());

        try
        {
            await repository.CreateAsync(booking, ct);
        }
        catch (BookingConflictException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "slot_conflict");
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }

        logger.LogInformation("[{Jira}] Booking {BookingId} for {Facility} {Range}",
            CampusTelemetry.JiraIssue, bookingId, request.FacilityId, range);

        return CreatedAtAction(nameof(Get), new { id = bookingId }, BookingResponse.From(booking));
    }

    /// <summary>Получить бронь.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BookingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        CampusTelemetry.Enrich(Activity.Current, id.ToString());

        var booking = await repository.GetAsync(id, ct);
        if (booking is null) return NotFound();

        Activity.Current?.SetTag(CampusTelemetry.TagFacilityId, booking.FacilityId);
        return Ok(BookingResponse.From(booking));
    }

    /// <summary>Отменить бронь (для sanity-броней — физическое удаление, шаг Cleanup).</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        CampusTelemetry.Enrich(Activity.Current, id.ToString());
        using var activity = CampusTelemetry.Source.StartActivity("SportsBooking.Cancel");
        CampusTelemetry.Enrich(activity, id.ToString());

        return await repository.CancelOrPurgeAsync(id, ct) ? NoContent() : NotFound();
    }

    private static bool TryResolveRange(CreateBookingRequest r, out TimeRange range, out string error)
    {
        range = default;
        error = string.Empty;
        try
        {
            if (r.Start is { } start && r.End is { } end)
            {
                range = new TimeRange(start, end);            // новый формат (v1.9+)
                return true;
            }
            if (r.TimeSlot is not null)
            {
                range = TimeRange.ParseLegacy(r.TimeSlot);    // старый формат (клиенты v1.8)
                return true;
            }
            error = "Укажите start/end (TimeRange) или timeSlot (устаревший формат).";
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            error = ex.Message;
            return false;
        }
    }
}

/// <summary>
/// Запрос: новый формат — start/end.
/// timeSlot ("2026-11-05 18:00-19:30") оставлен для совместимости с клиентами v1.8,
/// DEPRECATED — будет удалён в v1.10.0 вместе с 03_contract.sql.
/// </summary>
public sealed record CreateBookingRequest(
    string FacilityId,
    string StudentId,
    DateTimeOffset? Start = null,
    DateTimeOffset? End = null,
    string? TimeSlot = null);

public sealed record BookingResponse(
    Guid Id,
    string FacilityId,
    string StudentId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string TimeSlot,
    string Status)
{
    public static BookingResponse From(SportsBooking b) => new(
        b.Id, b.FacilityId, b.StudentId,
        b.TimeRange.Start.ToOffset(TimeRange.CampusOffset),
        b.TimeRange.End.ToOffset(TimeRange.CampusOffset),
        b.LegacyTimeSlot,
        b.Status.ToString());
}
