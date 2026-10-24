using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Campus.Arm.Updater.Infrastructure;

/// <summary>Общие настройки JSON (camelCase, комментарии и кириллица без экранирования).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };
}

/// <summary>Контрольные суммы для проверки целостности пакетов, файлов и резервных копий.</summary>
public static class Hashing
{
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>Повтор файловых операций при временных ошибках (файл занят антивирусом, сетевая папка «моргнула»).</summary>
public static class Retry
{
    public static T Io<T>(Func<T> action, AppLogger log, string what, int attempts = 3)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                return action();
            }
            catch (IOException ex) when (i < attempts)
            {
                log.Warn($"{what}: ошибка ввода-вывода ({ex.Message}), повтор {i}/{attempts - 1} через {i * 500} мс");
                Thread.Sleep(i * 500);
            }
        }
    }
}

/// <summary>Короткая запись путей в журнале: относительно текущего каталога, если это возможно.</summary>
public static class Paths
{
    public static string Rel(string path)
    {
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, path);
        return relative.StartsWith("..") || Path.IsPathRooted(relative) ? path : relative;
    }
}

/// <summary>Коды завершения утилиты (используются планировщиком/скриптами развёртывания).</summary>
public static class ExitCodes
{
    public const int Ok = 0;            // АРМ готов / операция выполнена
    public const int NotReady = 1;      // проверки не пройдены
    public const int RolledBack = 2;    // обновление откачено
    public const int Aborted = 3;       // обновление прервано до установки (пакет повреждён, сервер недоступен)
    public const int ConfigError = 4;   // ошибка настроек или аргументов
}
