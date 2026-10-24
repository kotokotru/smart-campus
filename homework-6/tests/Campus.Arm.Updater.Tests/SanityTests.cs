using System.Net;
using System.Text;
using Campus.Arm.Updater.Config;
using Campus.Arm.Updater.Demo;
using Campus.Arm.Updater.Health;
using Campus.Arm.Updater.Infrastructure;
using Campus.Arm.Updater.Update;

namespace Campus.Arm.Updater.Tests;

/// <summary>
/// Этап 4. Sanity-тесты: имитация установки обновления на стенде во временном каталоге,
/// проверка целостности данных, откат и формирование акта готовности.
/// Внешний API заменён заглушкой, поэтому тесты не зависят от интернета.
/// </summary>
public sealed class SanityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"arm-sanity-{Guid.NewGuid():N}");
    private readonly UpdaterOptions _options;
    private readonly AppLogger _log;

    public SanityTests()
    {
        Directory.CreateDirectory(_dir);
        var config = DemoEnvironment.Create(_dir);
        _options = UpdaterOptions.Load(config);
        _options.HealthCheck.ApiTimeoutSeconds = 1;
        _options.HealthCheck.ApiRetries = 2;
        _log = new AppLogger(_options.LogsRoot) { Quiet = true };
    }

    private ArmLayout Layout => new(_options.InstallRoot);

    /// <summary>Фабрика создаёт новый обработчик на каждую проверку (HttpClient освобождает его после запроса).</summary>
    private UpdateOrchestrator Orchestrator(Func<HttpMessageHandler>? api = null) =>
        new(_options, _log, api ?? (() => new StubApi(HttpStatusCode.OK, """{"ip":"95.24.13.7"}""")));

    [Fact]
    public async Task Update_GoodRelease_SwitchesSlot_KeepsData_AndActSaysReady()
    {
        var code = await Orchestrator().UpdateAsync("2.5.0");

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal("green", Layout.ReadActiveSlot());
        Assert.Equal("2.5.0", Layout.ReadVersion("green")!.Version);

        using var db = LocalDb.Open(Layout.Database, readOnly: true);
        Assert.Equal(2, LocalDb.GetSchemaVersion(db));
        Assert.True(LocalDb.ColumnExists(db, "access_events", "reader_id"));
        Assert.Equal(1200, LocalDb.CountRows(db, "access_events")["access_events"]);   // данные не потеряны

        var act = await File.ReadAllTextAsync(Directory.GetFiles(_options.ReportsRoot, "ACT_*.md").Single());
        Assert.Contains("ГОТОВ к промышленной эксплуатации", act);
        Assert.Single(Directory.GetDirectories(_options.BackupRoot));                    // резервная копия создана
    }

    [Fact]
    public async Task Update_BrokenMigration_RollsBack_RestoresDatabase()
    {
        Assert.Equal(ExitCodes.Ok, await Orchestrator().UpdateAsync("2.5.0"));

        var code = await Orchestrator().UpdateAsync("2.5.1");

        Assert.Equal(ExitCodes.RolledBack, code);
        Assert.Equal("green", Layout.ReadActiveSlot());                                 // снова 2.5.0
        Assert.Equal("2.5.0", Layout.ReadVersion(Layout.ReadActiveSlot())!.Version);

        using var db = LocalDb.Open(Layout.Database, readOnly: true);
        Assert.Equal(2, LocalDb.GetSchemaVersion(db));                                    // схема восстановлена из копии
        Assert.False(LocalDb.ColumnExists(db, "access_events", "readr_zone"));
        Assert.Equal(1200, LocalDb.CountRows(db, "access_events")["access_events"]);
    }

    [Fact]
    public async Task ManualRollback_AfterFailedUpdate_ReturnsToPreviousGoodVersion()
    {
        await Orchestrator().UpdateAsync("2.5.0");      // 2.4.0 → 2.5.0 (green)
        await Orchestrator().UpdateAsync("2.5.1");      // сбой, автооткат, слот blue очищен
        Assert.Null(Layout.ReadVersion("blue"));

        var code = await Orchestrator().RollbackAsync(); // ручной откат 2.5.0 → 2.4.0

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal("blue", Layout.ReadActiveSlot());
        Assert.Equal("2.4.0", Layout.ReadVersion("blue")!.Version);   // переустановлена с сервера
        using var db = LocalDb.Open(Layout.Database, readOnly: true);
        Assert.Equal(1, LocalDb.GetSchemaVersion(db));                  // данные из копии версии 2.4.0
        Assert.Equal(1200, LocalDb.CountRows(db, "access_events")["access_events"]);
    }

    [Fact]
    public async Task Update_CorruptedPackage_IsRejected_BeforeAnyChange()
    {
        var code = await Orchestrator().UpdateAsync("2.6.0");

        Assert.Equal(ExitCodes.Aborted, code);
        Assert.Equal("blue", Layout.ReadActiveSlot());
        Assert.False(Directory.Exists(_options.BackupRoot) && Directory.GetDirectories(_options.BackupRoot).Length > 0);
    }

    [Fact]
    public async Task HealthCheck_ApiTimeout_IsHandled_AndArmIsNotReady()
    {
        var code = await Orchestrator(() => new StubApi(delay: TimeSpan.FromSeconds(10))).HealthCheckAsync();

        Assert.Equal(ExitCodes.NotReady, code);
        var act = await File.ReadAllTextAsync(Directory.GetFiles(_options.ReportsRoot, "ACT_*.md").Single());
        Assert.Contains("таймаут", act);
        Assert.Contains("НЕ ГОТОВ", act);
    }

    [Fact]
    public async Task Update_ApiDownAfterSwitch_TriggersRollback()
    {
        var code = await Orchestrator(() => new StubApi(HttpStatusCode.ServiceUnavailable, "")).UpdateAsync("2.5.0");

        Assert.Equal(ExitCodes.RolledBack, code);
        Assert.Equal("blue", Layout.ReadActiveSlot());
        using var db = LocalDb.Open(Layout.Database, readOnly: true);
        Assert.Equal(1, LocalDb.GetSchemaVersion(db));
    }

    [Fact]
    public async Task HealthCheck_MissingReaderCard_FailsReaderOnly()
    {
        File.Delete(Layout.ReaderTestCard);
        var checks = new IHealthCheck[] { new DatabaseCheck(), new ReaderCheck(new EmulatedCardReader(Layout.ReaderTestCard)) };

        var results = await new HealthCheckRunner(checks).RunAsync(new HealthContext { Layout = Layout, Options = _options, Log = _log });

        Assert.Equal(HealthStatus.Pass, results[0].Status);
        Assert.Equal(HealthStatus.Fail, results[1].Status);
        Assert.All(results, r => Assert.True(r.LatencyMs >= 0));
    }

    [Fact]
    public void ExternalIp_IsMasked() => Assert.Equal("95.24.*.*", ApiCheck.Mask("95.24.13.7"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Заглушка внешнего API: заданный ответ или задержка (имитация сетевого таймаута).</summary>
    private sealed class StubApi(HttpStatusCode status = HttpStatusCode.OK, string body = "", TimeSpan? delay = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (delay is not null) await Task.Delay(delay.Value, ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
