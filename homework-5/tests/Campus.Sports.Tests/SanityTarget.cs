using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Campus.Sports.Tests;

/// <summary>
/// Цель sanity-прогона.
///  • SANITY_BASE_URL задан  → боевой стенд (https://campus.example/sports) сразу после релиза;
///  • SANITY_BASE_URL не задан → тот же сценарий in-process (WebApplicationFactory) — для CI.
/// </summary>
public sealed class SanityTarget : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;

    public HttpClient Client { get; private set; } = null!;
    public bool IsProduction { get; private set; }

    public Task InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("SANITY_BASE_URL");
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            IsProduction = true;
            Client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };

            var token = Environment.GetEnvironmentVariable("SANITY_API_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
                Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            _factory = new WebApplicationFactory<Program>();
            Client = _factory.CreateClient();
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }
}
