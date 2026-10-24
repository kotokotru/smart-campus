using System.Text.Json;
using Campus.Arm.Updater.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Campus.Arm.Updater.Update;

/// <summary>Сведения о резервной копии (backup.json внутри каталога копии).</summary>
public sealed class BackupInfo
{
    public string Path { get; set; } = "";
    public string Version { get; set; } = "";
    public string ActiveSlot { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    /// <summary>Относительный путь файла → SHA-256 (для проверки копии перед восстановлением).</summary>
    public Dictionary<string, string> Files { get; set; } = new();
}

/// <summary>
/// Резервное копирование локальных данных АРМ перед обновлением:
/// appsettings.json, кэш справочников и локальная БД SQLite.
/// БД копируется через онлайн-API SQLite (BackupDatabase) — получается согласованная копия,
/// даже если клиент АРМ в этот момент пишет в журнал проходов.
/// </summary>
public sealed class BackupService(ArmLayout layout, string backupRoot, AppLogger log, int keep = 5)
{
    public BackupInfo Create(string version, string activeSlot)
    {
        var dir = Path.Combine(backupRoot, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_v{version}");
        Directory.CreateDirectory(dir);
        log.Info($"[BACKUP] Каталог копии: {Paths.Rel(dir)}");

        // 1. Конфигурация АРМ
        Retry.Io(() => { File.Copy(layout.AppSettings, Path.Combine(dir, "appsettings.json")); return 0; },
            log, "Копирование appsettings.json");

        // 2. Кэш справочников
        CopyDirectory(layout.CacheDir, Path.Combine(dir, "cache"));

        // 3. Локальная БД — онлайн-копия средствами SQLite
        using (var source = LocalDb.Open(layout.Database, readOnly: true))
        using (var target = new SqliteConnection($"Data Source={Path.Combine(dir, "campus-local.db")};Pooling=False"))
        {
            target.Open();
            source.BackupDatabase(target);
        }

        // 4. Манифест копии с контрольными суммами
        var info = new BackupInfo
        {
            Path = dir,
            Version = version,
            ActiveSlot = activeSlot,
            CreatedAt = DateTime.Now
        };
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            info.Files[System.IO.Path.GetRelativePath(dir, file)] = Hashing.Sha256File(file);

        File.WriteAllText(Path.Combine(dir, "backup.json"), JsonSerializer.Serialize(info, Json.Options));

        Verify(info);
        log.Ok($"[BACKUP] Резервная копия создана и проверена: {info.Files.Count} файлов (appsettings, кэш, SQLite)");

        Rotate();
        return info;
    }

    /// <summary>Сверка контрольных сумм копии. Копию, не прошедшую проверку, восстанавливать нельзя.</summary>
    public void Verify(BackupInfo info)
    {
        foreach (var (relative, expected) in info.Files)
        {
            var actual = Hashing.Sha256File(Path.Combine(info.Path, relative));
            if (actual != expected)
                throw new InvalidDataException($"Резервная копия повреждена: {relative} (SHA-256 не совпадает)");
        }
    }

    /// <summary>Восстановление локальных данных из резервной копии (часть плана отката).</summary>
    public void Restore(BackupInfo info)
    {
        Verify(info);
        log.Warn($"[ROLLBACK] Восстановление данных из {Paths.Rel(info.Path)}");

        SqliteConnection.ClearAllPools();

        Retry.Io(() => { File.Copy(Path.Combine(info.Path, "appsettings.json"), layout.AppSettings, overwrite: true); return 0; },
            log, "Восстановление appsettings.json");

        if (Directory.Exists(layout.CacheDir)) Directory.Delete(layout.CacheDir, recursive: true);
        CopyDirectory(Path.Combine(info.Path, "cache"), layout.CacheDir);

        // Служебные файлы журнала SQLite от новой версии не должны «накатиться» на старую БД
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(layout.Database + suffix)) File.Delete(layout.Database + suffix);

        Retry.Io(() => { File.Copy(Path.Combine(info.Path, "campus-local.db"), layout.Database, overwrite: true); return 0; },
            log, "Восстановление campus-local.db");

        log.Ok("[ROLLBACK] Данные восстановлены: appsettings.json, кэш, локальная БД");
    }

    /// <summary>Хранить только последние N копий.</summary>
    private void Rotate()
    {
        var old = new DirectoryInfo(backupRoot).GetDirectories()
            .OrderByDescending(d => d.Name, StringComparer.Ordinal)
            .Skip(keep);
        foreach (var dir in old)
        {
            try
            {
                dir.Delete(recursive: true);
                log.Info($"[BACKUP] Удалена устаревшая копия {dir.Name}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"[BACKUP] Не удалось удалить старую копию {dir.Name}: {ex.Message}");
            }
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        if (!Directory.Exists(from)) return;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
