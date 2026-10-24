// ============================================================================================
//  Campus.Arm.Updater — утилита обновления и проверки готовности АРМ оператора
//  ИС «Умный кампус». Вариант: «Тестер Health Check после обновления» — цепочка проверок
//  (БД, API, считыватель), журнал задержек (Latency), акт готовности АРМ.
//
//  Команды:
//    demo-init   [--force]                    создать стенд опытной эксплуатации (АРМ 2.4.0 + сервер обновлений)
//    status                                   версия, активный слот, состояние локальной БД
//    check                                    релизы на сервере обновлений и их доступность для кольца АРМ
//    update      [--version X.Y.Z]            обновление: backup → установка → Health Check → акт (откат при сбое)
//    healthcheck                              только цепочка проверок и акт готовности
//    rollback                                 ручной откат на версию из резервного слота
//  Общие параметры:
//    --config <путь>        файл настроек (по умолчанию ./updater.json)
//    --api-url <url>        переопределить адрес API (например, для имитации недоступности сети)
//    --api-timeout <сек>    таймаут одной попытки запроса к API
//    --reader <Emulator|Serial>
// ============================================================================================

using System.Text;
using Campus.Arm.Updater.Config;
using Campus.Arm.Updater.Demo;
using Campus.Arm.Updater.Infrastructure;
using Campus.Arm.Updater.Update;

Console.OutputEncoding = Encoding.UTF8;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var opts = ParseOptions(args.Skip(1).ToArray());

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;              // корректно завершаем текущий шаг вместо «убийства» процесса
    cts.Cancel();
};

PrintBanner(command);

try
{
    if (command is "help" or "-h" or "--help")
    {
        PrintHelp();
        return ExitCodes.Ok;
    }

    if (command == "demo-init")
    {
        var config = DemoEnvironment.Create(Directory.GetCurrentDirectory(), force: opts.ContainsKey("force"));
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Стенд создан. Настройки: {config}");
        Console.WriteLine("АРМ ARM-KPP-01: версия 2.4.0 (слот blue). На сервере обновлений: 2.5.0, 2.5.1, 2.6.0.");
        Console.ResetColor();
        return ExitCodes.Ok;
    }

    var options = UpdaterOptions.Load(opts.GetValueOrDefault("config", "updater.json"));
    if (opts.TryGetValue("api-url", out var apiUrl)) options.HealthCheck.ApiUrl = apiUrl;
    if (opts.TryGetValue("api-timeout", out var timeout)) options.HealthCheck.ApiTimeoutSeconds = int.Parse(timeout);
    if (opts.TryGetValue("reader", out var reader)) options.HealthCheck.Reader.Mode = reader;

    var log = new AppLogger(options.LogsRoot);
    var orchestrator = new UpdateOrchestrator(options, log);

    return command switch
    {
        "status" => orchestrator.Status(),
        "check" => orchestrator.Check(),
        "update" => await orchestrator.UpdateAsync(opts.GetValueOrDefault("version"), cts.Token),
        "healthcheck" => await orchestrator.HealthCheckAsync(cts.Token),
        "rollback" => await orchestrator.RollbackAsync(cts.Token),
        _ => Unknown(command)
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Операция прервана пользователем (Ctrl+C).");
    return ExitCodes.Aborted;
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or FormatException or ArgumentException)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"Ошибка настройки/данных: {ex.Message}");
    Console.ResetColor();
    return ExitCodes.ConfigError;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"Ошибка файловой системы: {ex.Message}");
    Console.ResetColor();
    return ExitCodes.Aborted;
}

// --------------------------------------------------------------------------------------------

static Dictionary<string, string> ParseOptions(string[] a)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--")) continue;
        var key = a[i][2..];
        var hasValue = i + 1 < a.Length && !a[i + 1].StartsWith("--");
        result[key] = hasValue ? a[++i] : "true";
    }
    return result;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Неизвестная команда: {command}");
    PrintHelp();
    return ExitCodes.ConfigError;
}

static void PrintBanner(string command)
{
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine($"Campus.Arm.Updater 1.0 · ИС «Умный кампус» · АРМ оператора · команда: {command}");
    Console.ResetColor();
}

static void PrintHelp()
{
    Console.WriteLine("""
        Использование: Campus.Arm.Updater <команда> [параметры]

          demo-init [--force]         создать стенд (АРМ 2.4.0 + сервер обновлений с релизами 2.5.0, 2.5.1, 2.6.0)
          status                      текущая версия, слот Blue-Green, состояние локальной БД
          check                       релизы на сервере и их доступность для кольца АРМ (canary/stable)
          update [--version X.Y.Z]    обновить АРМ (резервная копия → установка → Health Check → акт / откат)
          healthcheck                 выполнить цепочку проверок и сформировать акт готовности
          rollback                    вручную вернуть предыдущую версию

          --config <путь>             файл настроек (по умолчанию ./updater.json)
          --api-url <url>             адрес API для проверки (по умолчанию https://api.ipify.org?format=json)
          --api-timeout <сек>         таймаут одной попытки обращения к API
          --reader <Emulator|Serial>  режим считывателя карт

        Коды завершения: 0 — готов, 1 — не готов, 2 — выполнен откат, 3 — прервано, 4 — ошибка настроек
        """);
}
