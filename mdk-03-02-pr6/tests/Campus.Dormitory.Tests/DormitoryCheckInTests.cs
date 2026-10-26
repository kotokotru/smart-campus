using System.Net;
using System.Net.Http.Json;
using Campus.Dormitory.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace Campus.Dormitory.Tests;

/// <summary>
/// Тесты модуля «Общежитие и заселение» (вариант 10).
/// xUnit создаёт новый экземпляр класса на каждый тест, поэтому у каждого теста своя фабрика и своя база.
/// </summary>
public class DormitoryCheckInTests : IDisposable
{
    private const string Url = "/api/v1/dormitory/checkin";
    private readonly CustomWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public DormitoryCheckInTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // Тест 1. Позитивный (Functional Suitability)
    [Fact]
    public async Task PostCheckIn_ValidData_Returns200AndSavesRecord()
    {
        var request = new DormitoryCheckIn { ResidentPassport = "4015 123456", BuildingNumber = 3, RoomNumber = 412 };

        var post = await _client.PostAsJsonAsync(Url, request);

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var created = await post.Content.ReadFromJsonAsync<DormitoryCheckIn>();
        Assert.NotNull(created);
        Assert.True(created.Id > 0);
        Assert.Equal("4015 123456", created.ResidentPassport);

        var get = await _client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var all = await get.Content.ReadFromJsonAsync<List<DormitoryCheckIn>>();
        var saved = Assert.Single(all!);
        Assert.Equal(created.Id, saved.Id);
        Assert.Equal(3, saved.BuildingNumber);
        Assert.Equal(412, saved.RoomNumber);
    }

    // Тест 2. Негативный (Validation): паспорт не передан, корпус вне диапазона 1–9
    [Fact]
    public async Task PostCheckIn_NullPassportAndInvalidBuilding_Returns400BadRequest()
    {
        var request = new DormitoryCheckIn { ResidentPassport = null, BuildingNumber = 10, RoomNumber = 101 };

        var post = await _client.PostAsJsonAsync(Url, request);

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        var problem = await post.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains(problem.Errors.Keys, k => k.Equals(nameof(DormitoryCheckIn.ResidentPassport), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(problem.Errors.Keys, k => k.Equals(nameof(DormitoryCheckIn.BuildingNumber), StringComparison.OrdinalIgnoreCase));

        // некорректная запись не должна попасть в базу
        var all = await _client.GetFromJsonAsync<List<DormitoryCheckIn>>(Url);
        Assert.Empty(all!);
    }

    // Тест 3. Интеграционный (Isolation): у каждого теста и каждой фабрики своя чистая база
    [Fact]
    public async Task Database_IsEmptyAndIsolated_ForEachTestAndFactory()
    {
        // база этого теста пуста, хотя другие тесты класса уже добавляли записи
        var before = await _client.GetFromJsonAsync<List<DormitoryCheckIn>>(Url);
        Assert.Empty(before!);

        // запись во второй, независимой фабрике
        using (var otherFactory = new CustomWebApplicationFactory())
        using (var otherClient = otherFactory.CreateClient())
        {
            var post = await otherClient.PostAsJsonAsync(Url,
                new DormitoryCheckIn { ResidentPassport = "4020 654321", BuildingNumber = 1, RoomNumber = 7 });
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
            Assert.Single((await otherClient.GetFromJsonAsync<List<DormitoryCheckIn>>(Url))!);
        }

        // в базе текущего теста по-прежнему ничего нет
        var after = await _client.GetFromJsonAsync<List<DormitoryCheckIn>>(Url);
        Assert.Empty(after!);
    }
}
