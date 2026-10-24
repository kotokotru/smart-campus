using System.IO.Compression;
using Campus.Arm.Updater.Infrastructure;

namespace Campus.Arm.Updater.Update;

/// <summary>
/// Установка новой версии в неактивный слот (Blue-Green) и применение миграций локальной БД.
/// Работающая версия в активном слоте не затрагивается, пока новая не проверена.
/// </summary>
public sealed class BlueGreenInstaller(ArmLayout layout, AppLogger log)
{
    /// <summary>Распаковка пакета в слот и сверка SHA-256 каждого файла с version.json.</summary>
    public AppVersionInfo InstallToSlot(string packageZip, string slot)
    {
        var dir = layout.SlotDir(slot);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        try
        {
            ZipFile.ExtractToDirectory(packageZip, dir);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"Пакет {Path.GetFileName(packageZip)} не является корректным zip-архивом: {ex.Message}", ex);
        }

        var info = layout.ReadVersion(slot)
                   ?? throw new InvalidDataException("В пакете отсутствует version.json");

        foreach (var (relative, expected) in info.Files)
        {
            var file = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file))
                throw new InvalidDataException($"В пакете отсутствует файл {relative}");
            if (Hashing.Sha256File(file) != expected)
                throw new InvalidDataException($"Файл {relative} повреждён (SHA-256 не совпадает)");
        }

        log.Ok($"[INSTALL] Версия {info.Version} распакована в слот '{slot}', проверено файлов: {info.Files.Count}");
        return info;
    }

    /// <summary>
    /// Применение SQL-миграций из папки migrations/ пакета. Каждая миграция — в своей транзакции;
    /// номер миграции берётся из имени файла (003_add_reader_zone.sql → 3).
    /// </summary>
    public int ApplyMigrations(string slot)
    {
        var migrationsDir = Path.Combine(layout.SlotDir(slot), "migrations");
        using var connection = LocalDb.Open(layout.Database);
        var current = LocalDb.GetSchemaVersion(connection);

        if (!Directory.Exists(migrationsDir)) return current;

        foreach (var file in Directory.GetFiles(migrationsDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (!int.TryParse(name.Split('_')[0], out var number))
                throw new InvalidDataException($"Некорректное имя миграции: {name}");
            if (number <= current) continue;

            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = File.ReadAllText(file) + $";\nUPDATE schema_info SET version = {number};";
            cmd.ExecuteNonQuery();
            tx.Commit();

            current = number;
            log.Info($"[MIGRATE] Применена миграция {name}, версия схемы БД = {current}");
        }
        return current;
    }
}
