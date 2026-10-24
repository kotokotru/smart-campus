using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Campus.Arm.Updater.Config;
using Campus.Arm.Updater.Infrastructure;

namespace Campus.Arm.Updater.Health;

// Проверка 1. Целостность установки и данных

/// <summary>
/// Проверяет, что активный слот содержит все файлы пакета без искажений,
/// appsettings.json читается и содержит обязательные ключи,
/// а количество записей в локальной БД не уменьшилось после обновления.
/// </summary>
public sealed class IntegrityCheck : IHealthCheck
{
    private static readonly string[] RequiredSettings = ["ArmId", "CentralApiUrl", "ReaderMode"];

    public string Code => "INTEGRITY";
    public string Name => "Целостность установки и данных";
    public bool Critical => true;
    public int WarnLatencyMs(HealthCheckOptions o) => 0;

    public Task<string> RunAsync(HealthContext ctx, CancellationToken ct)
    {
        var slot = ctx.Layout.ReadActiveSlot();
        var version = ctx.Layout.ReadVersion(slot)
                      ?? throw new InvalidDataException($"В активном слоте '{slot}' нет version.json");

        // Файлы приложения
        foreach (var (relative, expected) in version.Files)
        {
            var file = Path.Combine(ctx.Layout.SlotDir(slot), relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) throw new FileNotFoundException("Отсутствует файл приложения", relative);
            if (Hashing.Sha256File(file) != expected)
                throw new InvalidDataException($"Файл {relative} изменён (SHA-256 не совпадает)");
        }

        // Конфигурация АРМ
        using (var doc = JsonDocument.Parse(File.ReadAllText(ctx.Layout.AppSettings)))
        {
            foreach (var key in RequiredSettings)
                if (!doc.RootElement.TryGetProperty(key, out _))
                    throw new InvalidDataException($"В appsettings.json нет обязательного ключа '{key}'");
        }

        // Сохранность данных: записей не меньше, чем до обновления
        using var db = LocalDb.Open(ctx.Layout.Database, readOnly: true);
        var rowsNow = LocalDb.CountRows(db, LocalDb.KeyTables);
        if (ctx.RowsBefore is not null)
        {
            foreach (var (table, before) in ctx.RowsBefore)
                if (rowsNow.TryGetValue(table, out var now) && now < before)
                    throw new InvalidDataException($"Потеря данных в таблице {table}: было {before}, стало {now}");
        }

        var rows = string.Join(", ", rowsNow.Select(kv => $"{kv.Key}={kv.Value}"));
        return Task.FromResult($"v{version.Version}, слот {slot}, файлов {version.Files.Count}; {rows}");
    }
}

// Проверка 2. Локальная БД SQLite

/// <summary>
/// PRAGMA integrity_check, соответствие версии схемы и обязательных колонок версии клиента,
/// тестовая запись в транзакции с откатом (БД доступна на запись).
/// </summary>
public sealed class DatabaseCheck : IHealthCheck
{
    public string Code => "DB";
    public string Name => "Локальная БД SQLite";
    public bool Critical => true;
    public int WarnLatencyMs(HealthCheckOptions o) => o.DatabaseWarnMs;

    public Task<string> RunAsync(HealthContext ctx, CancellationToken ct)
    {
        var version = ctx.Layout.ReadVersion(ctx.Layout.ReadActiveSlot())
                      ?? throw new InvalidDataException("Не удалось определить версию клиента");

        using var db = LocalDb.Open(ctx.Layout.Database);

        var integrity = LocalDb.Scalar(db, "PRAGMA integrity_check")?.ToString();
        if (integrity != "ok")
            throw new InvalidDataException($"PRAGMA integrity_check: {integrity}");

        var schema = LocalDb.GetSchemaVersion(db);
        if (schema != version.SchemaVersion)
            throw new InvalidDataException($"версия схемы БД {schema}, клиент v{version.Version} ожидает {version.SchemaVersion}");

        foreach (var required in version.RequiredColumns)
        {
            var parts = required.Split('.');
            if (parts.Length != 2 || !LocalDb.ColumnExists(db, parts[0], parts[1]))
                throw new InvalidDataException($"в БД нет колонки {required}, необходимой версии {version.Version}");
        }

        // Пробная запись: БД не только читается, но и принимает новые события прохода
        using (var tx = db.BeginTransaction())
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO access_events (card_uid, direction, event_time) VALUES ('HEALTHCHECK', 'IN', datetime('now'))";
            cmd.ExecuteNonQuery();
            tx.Rollback();
        }

        var events = LocalDb.Scalar(db, "SELECT COUNT(*) FROM access_events");
        return Task.FromResult($"integrity_check=ok, схема v{schema}, событий {events}, запись OK");
    }
}

// Проверка 3. Центральный API (по варианту — https://api.ipify.org?format=json)

/// <summary>
/// HTTP-запрос к API с таймаутом на каждую попытку и повторами с экспоненциальной паузой.
/// Обрабатываются: таймаут (network timeout), ошибки соединения/DNS, HTTP-коды ошибок,
/// некорректный JSON. Внешний IP в журнале маскируется.
/// </summary>
public sealed class ApiCheck(Func<HttpMessageHandler>? handlerFactory = null) : IHealthCheck
{
    public string Code => "API";
    public string Name => "Центральный API";
    public bool Critical => true;
    public int WarnLatencyMs(HealthCheckOptions o) => o.ApiWarnMs;

    public async Task<string> RunAsync(HealthContext ctx, CancellationToken ct)
    {
        var o = ctx.Options.HealthCheck;
        var attempts = Math.Max(1, o.ApiRetries);

        using var http = handlerFactory is null ? new HttpClient() : new HttpClient(handlerFactory(), disposeHandler: true);
        http.Timeout = Timeout.InfiniteTimeSpan;              // таймаут задаём на каждую попытку через CancellationToken
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Campus.Arm.Updater/1.0");

        var lastError = "";
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(o.ApiTimeoutSeconds));
            var sw = Stopwatch.StartNew();
            try
            {
                using var response = await http.GetAsync(o.ApiUrl, cts.Token);
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                sw.Stop();

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

                using var doc = JsonDocument.Parse(body);
                var ip = doc.RootElement.GetProperty("ip").GetString();
                if (!IPAddress.TryParse(ip, out _))
                    throw new FormatException($"ответ не содержит корректный IP: '{ip}'");

                ctx.Log.Info($"[API] попытка {attempt}/{attempts}: HTTP {(int)response.StatusCode}, {sw.ElapsedMilliseconds} мс, внешний IP {Mask(ip!)}");
                return $"GET {o.ApiUrl} → 200 OK, {sw.ElapsedMilliseconds} мс (попытка {attempt}/{attempts}), IP {Mask(ip!)}";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = $"таймаут {o.ApiTimeoutSeconds} с";
            }
            catch (HttpRequestException ex)
            {
                lastError = $"сетевая ошибка: {ex.Message}";
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                lastError = $"некорректный ответ API: {ex.Message}";
            }

            ctx.Log.Warn($"[API] попытка {attempt}/{attempts} неудачна через {sw.ElapsedMilliseconds} мс: {lastError}");
            if (attempt < attempts)
            {
                var pause = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
                ctx.Log.Info($"[API] повтор через {pause.TotalMilliseconds} мс");
                await Task.Delay(pause, ct);
            }
        }

        throw new HttpRequestException($"API недоступен после {attempts} попыток ({lastError})");
    }

    /// <summary>Маскирование IP: 95.24.13.7 → 95.24.*.* (адрес не попадает в отчёты целиком).</summary>
    internal static string Mask(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length == 4) return $"{parts[0]}.{parts[1]}.*.*";
        var v6 = ip.Split(':');
        return v6.Length > 2 ? $"{v6[0]}:{v6[1]}:*" : "*";
    }
}

// Проверка 4. Считыватель карт

/// <summary>Абстракция считывателя пропусков/студенческих билетов.</summary>
public interface ICardReader
{
    string Description { get; }
    Task<string> ReadCardAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>Эмулятор считывателя для стенда опытной эксплуатации: UID тестовой карты берётся из файла.</summary>
public sealed class EmulatedCardReader(string testCardFile) : ICardReader
{
    public string Description => "эмулятор";

    public async Task<string> ReadCardAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (!File.Exists(testCardFile))
            throw new FileNotFoundException("Файл тестовой карты эмулятора не найден", testCardFile);
        await Task.Delay(40, ct);                 // имитация времени чтения карты
        return (await File.ReadAllTextAsync(testCardFile, ct)).Trim();
    }
}

/// <summary>Реальный считыватель на COM-порту: команда READ, ответ — UID карты строкой.</summary>
public sealed class SerialCardReader(string portName, int baudRate) : ICardReader
{
    public string Description => $"{portName}, {baudRate} бод";

    public Task<string> ReadCardAsync(TimeSpan timeout, CancellationToken ct) => Task.Run(() =>
    {
        using var port = new System.IO.Ports.SerialPort(portName, baudRate)
        {
            ReadTimeout = (int)timeout.TotalMilliseconds,
            WriteTimeout = (int)timeout.TotalMilliseconds,
            NewLine = "\r\n"
        };
        port.Open();                              // UnauthorizedAccessException — порт занят другой программой
        port.WriteLine("READ");
        return port.ReadLine().Trim();            // TimeoutException — считыватель не ответил
    }, ct);
}

/// <summary>Проверка считывателя: чтение тестовой карты и проверка формата UID (4 или 7 байт).</summary>
public sealed partial class ReaderCheck(ICardReader reader) : IHealthCheck
{
    [GeneratedRegex("^([0-9A-F]{8}|[0-9A-F]{14})$")]
    private static partial Regex UidFormat();

    public string Code => "READER";
    public string Name => "Считыватель карт";
    public bool Critical => true;
    public int WarnLatencyMs(HealthCheckOptions o) => o.ReaderWarnMs;

    public async Task<string> RunAsync(HealthContext ctx, CancellationToken ct)
    {
        var uid = await reader.ReadCardAsync(TimeSpan.FromMilliseconds(ctx.Options.HealthCheck.Reader.TimeoutMs), ct);
        if (!UidFormat().IsMatch(uid))
            throw new InvalidDataException($"некорректный UID карты: '{uid}'");
        return $"{reader.Description}: прочитана тестовая карта UID {uid[..4]}****";
    }

    /// <summary>Выбор реализации по настройке Reader.Mode.</summary>
    public static ICardReader Create(ReaderOptions o, string emulatorCardFile) =>
        o.Mode.Equals("Serial", StringComparison.OrdinalIgnoreCase)
            ? new SerialCardReader(o.PortName, o.BaudRate)
            : new EmulatedCardReader(emulatorCardFile);
}
