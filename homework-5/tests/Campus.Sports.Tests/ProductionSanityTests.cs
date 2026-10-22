using System.Net;
using System.Net.Http.Json;
using Xunit;
using Xunit.Abstractions;

namespace Campus.Sports.Tests;

/// <summary>
/// Production Sanity-тест релиза v1.9.0 (CAMPUS-110).
/// Точечная проверка боевого сценария сразу после выката: создание тестовой брони
/// площадки спорткомплекса в новом формате TimeRange → чтение → ОБЯЗАТЕЛЬНАЯ очистка.
///
/// Запуск после релиза:
///   SANITY_BASE_URL=https://campus.example/sports SANITY_API_TOKEN=*** \
///   dotnet test --filter "Category=Sanity"
/// </summary>
[Trait("Category", "Sanity")]
public sealed class ProductionSanityTests(SanityTarget target, ITestOutputHelper output)
    : IClassFixture<SanityTarget>, IAsyncLifetime
{
    /// <summary>Служебная площадка: не видна студентам в каталоге, не влияет на реальные брони.</summary>
    private const string SanityFacility = "sanity-court-00";
    private const string SanityStudent = "sanity-bot";

    private readonly List<Guid> _created = new();

    private sealed record BookingDto(
        Guid Id, string FacilityId, string StudentId,
        DateTimeOffset Start, DateTimeOffset End, string TimeSlot, string Status);

    [Fact(DisplayName = "Sanity: бронь в формате TimeRange создаётся, читается и удаляется")]
    public async Task CreateBooking_InTimeRangeFormat_ThenCleanup()
    {
        // Arrange — слот 15 минут далеко в будущем, в ночные часы (03:00 МСК), случайный день,
        // чтобы параллельные прогоны и реальные пользователи не пересекались.
        var dayUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero)
            .AddDays(365 + Random.Shared.Next(0, 3000));
        var start = dayUtc.ToOffset(TimeSpan.FromHours(3));          // 00:00 UTC = 03:00 МСК
        var end = start.AddMinutes(15);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/sports/bookings")
        {
            Content = JsonContent.Create(new
            {
                facilityId = SanityFacility,
                studentId = SanityStudent,
                start,
                end
            })
        };
        request.Headers.Add("X-Campus-Sanity", "true");   // помечает бронь как тестовую (is_sanity_test)

        // Act 1 — создание
        using var createResponse = await target.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<BookingDto>();
        Assert.NotNull(created);
        _created.Add(created!.Id);                          // регистрируем для Cleanup ДО остальных проверок
        output.WriteLine($"[{(target.IsProduction ? "PROD" : "in-process")}] created booking {created.Id}");

        // Assert — новый формат сохранился без искажений, dual-write заполнил старое поле
        Assert.Equal(start, created.Start);
        Assert.Equal(end, created.End);
        Assert.Equal($"{start:yyyy-MM-dd} 03:00-03:15", created.TimeSlot);
        Assert.Equal("Active", created.Status);

        // Act 2 — чтение с другого эндпоинта (проверяем путь чтения из time_range)
        var fetched = await target.Client.GetFromJsonAsync<BookingDto>($"api/sports/bookings/{created.Id}");
        Assert.Equal(created, fetched);

        // Act 3 — повторная бронь того же слота должна дать 409 (проверка пересечения интервалов)
        using var duplicate = new HttpRequestMessage(HttpMethod.Post, "api/sports/bookings")
        {
            Content = JsonContent.Create(new { facilityId = SanityFacility, studentId = SanityStudent, start, end })
        };
        duplicate.Headers.Add("X-Campus-Sanity", "true");
        using var duplicateResponse = await target.Client.SendAsync(duplicate);
        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// CLEANUP: выполняется ВСЕГДА — даже если проверки упали.
    /// Удаляет созданные тестовые брони и убеждается, что следов в боевой БД не осталось.
    /// </summary>
    public async Task DisposeAsync()
    {
        foreach (var id in _created)
        {
            using var delete = await target.Client.DeleteAsync($"api/sports/bookings/{id}");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

            using var check = await target.Client.GetAsync($"api/sports/bookings/{id}");
            Assert.Equal(HttpStatusCode.NotFound, check.StatusCode);
            output.WriteLine($"cleanup: booking {id} removed");
        }
    }
}
