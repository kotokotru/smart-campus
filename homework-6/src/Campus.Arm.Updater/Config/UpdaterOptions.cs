using System.Text.Json;
using Campus.Arm.Updater.Infrastructure;

namespace Campus.Arm.Updater.Config;

/// <summary>
/// Настройки утилиты (файл updater.json рядом с утилитой или путь из --config).
/// Все относительные пути считаются от папки, в которой лежит файл настроек.
/// </summary>
public sealed class UpdaterOptions
{
    /// <summary>Идентификатор АРМ (попадает в журнал и акт готовности).</summary>
    public string ArmId { get; set; } = "ARM-KPP-01";

    /// <summary>
    /// Кольцо развёртывания (Canary Deployment): "canary" — пилотные АРМ получают релиз первыми,
    /// "stable" — остальные АРМ получают релиз после успешной опытной эксплуатации на canary.
    /// </summary>
    public string Ring { get; set; } = "canary";

    /// <summary>Каталог установки клиента АРМ (слоты blue/green, локальные данные).</summary>
    public string InstallRoot { get; set; } = "demo-env/arm";

    /// <summary>Источник обновлений — сетевая папка центрального сервера (\\server\updates) или локальный каталог.</summary>
    public string UpdateSource { get; set; } = "demo-env/update-server";

    public string BackupRoot { get; set; } = "demo-env/backups";
    public string ReportsRoot { get; set; } = "demo-env/reports";
    public string LogsRoot { get; set; } = "demo-env/logs";

    /// <summary>Сколько последних резервных копий хранить.</summary>
    public int BackupsToKeep { get; set; } = 5;

    public HealthCheckOptions HealthCheck { get; set; } = new();

    /// <summary>Загрузка настроек. Ошибки формата превращаются в понятное сообщение для администратора.</summary>
    public static UpdaterOptions Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Файл настроек не найден: {path}. Выполните команду demo-init или укажите --config.", path);

        UpdaterOptions? options;
        try
        {
            options = JsonSerializer.Deserialize<UpdaterOptions>(File.ReadAllText(path), Json.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Ошибка в файле настроек {path}: {ex.Message}", ex);
        }

        options ??= new UpdaterOptions();
        options.ResolvePaths(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return options;
    }

    /// <summary>Приводит относительные пути к абсолютным.</summary>
    public void ResolvePaths(string baseDir)
    {
        InstallRoot = Path.GetFullPath(InstallRoot, baseDir);
        UpdateSource = Path.GetFullPath(UpdateSource, baseDir);
        BackupRoot = Path.GetFullPath(BackupRoot, baseDir);
        ReportsRoot = Path.GetFullPath(ReportsRoot, baseDir);
        LogsRoot = Path.GetFullPath(LogsRoot, baseDir);
    }
}

/// <summary>Параметры цепочки проверок Health Check.</summary>
public sealed class HealthCheckOptions
{
    /// <summary>Внешний API, доступность которого проверяется (по варианту — api.ipify.org).</summary>
    public string ApiUrl { get; set; } = "https://api.ipify.org?format=json";

    /// <summary>Таймаут одной попытки запроса к API, секунд.</summary>
    public int ApiTimeoutSeconds { get; set; } = 5;

    /// <summary>Количество попыток (повтор с экспоненциальной паузой 0,5 → 1 → 2 с).</summary>
    public int ApiRetries { get; set; } = 3;

    /// <summary>Пороги задержки (Latency), после которых проверка получает статус «Предупреждение».</summary>
    public int DatabaseWarnMs { get; set; } = 200;
    public int ApiWarnMs { get; set; } = 1500;
    public int ReaderWarnMs { get; set; } = 500;

    public ReaderOptions Reader { get; set; } = new();
}

/// <summary>Параметры считывателя карт (турникет / пропускной пункт).</summary>
public sealed class ReaderOptions
{
    /// <summary>"Emulator" — эмулятор (стенд, опытная эксплуатация), "Serial" — реальный считыватель на COM-порту.</summary>
    public string Mode { get; set; } = "Emulator";
    public string PortName { get; set; } = "COM3";
    public int BaudRate { get; set; } = 9600;
    public int TimeoutMs { get; set; } = 2000;
}
