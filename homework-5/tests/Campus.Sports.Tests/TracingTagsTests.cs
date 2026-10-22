using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Campus.Sports.Api.Telemetry;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Campus.Sports.Tests;

/// <summary>Проверка блока 1: в span'ах присутствуют jira.issue и доменный идентификатор брони.</summary>
public sealed class TracingTagsTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ActivityListener _listener;
    private readonly ConcurrentBag<Activity> _stopped = new();

    public TracingTagsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is CampusTelemetry.SourceName or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => _stopped.Add(a)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [Fact]
    public async Task CreateBooking_SetsJiraIssueAndBookingIdTags()
    {
        var client = _factory.CreateClient();
        var start = new DateTimeOffset(2030, 3, 1, 18, 0, 0, TimeSpan.FromHours(3));

        var response = await client.PostAsJsonAsync("api/sports/bookings", new
        {
            facilityId = "gym-hall-1",
            studentId = "st-2023-0917",
            start,
            end = start.AddMinutes(90)
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var bookingId = body!["id"].ToString();

        // доменный span
        var domain = Assert.Single(_stopped, a =>
            a.OperationName == "SportsBooking.Create" &&
            (string?)a.GetTagItem(CampusTelemetry.TagBookingId) == bookingId);
        Assert.Equal("CAMPUS-110", domain.GetTagItem("jira.issue"));
        Assert.Equal("gym-hall-1", domain.GetTagItem("campus.facility_id"));
        Assert.NotNull(domain.GetTagItem("campus.time_range.start"));

        // span HTTP-запроса (Activity.Current в контроллере) — родитель доменного span
        var http = Assert.Single(_stopped, a => a.SpanId == domain.ParentSpanId);
        Assert.Equal("CAMPUS-110", http.GetTagItem("jira.issue"));
        Assert.Equal(bookingId, http.GetTagItem("campus.booking_id"));
    }

    public void Dispose() => _listener.Dispose();
}
