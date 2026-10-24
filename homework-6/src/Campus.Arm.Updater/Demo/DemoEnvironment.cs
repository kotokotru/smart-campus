using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Campus.Arm.Updater.Infrastructure;
using Campus.Arm.Updater.Update;
using Microsoft.Data.Sqlite;

namespace Campus.Arm.Updater.Demo;

/// <summary>
/// Стенд опытной эксплуатации: создаёт АРМ с версией 2.4.0 и «сервер обновлений» с тремя релизами.
/// <list type="bullet">
/// <item>2.5.0 — штатный релиз (canary + stable): миграция добавляет колонку reader_id;</item>
/// <item>2.5.1 — релиз с ошибкой в миграции (canary): после установки не проходит проверку БД → откат;</item>
/// <item>2.6.0 — повреждённый пакет (canary): SHA-256 не совпадает с манифестом → установка не начинается.</item>
/// </list>
/// </summary>
public static class DemoEnvironment
{
    private sealed record PackageSpec(string Version, int Schema, string[] RequiredColumns,
        Dictionary<string, string> Migrations, string[] Rings, string Notes, bool Corrupt = false);

    private static readonly PackageSpec[] Packages =
    [
        new("2.4.0", 1, ["access_events.card_uid"], new(), ["canary", "stable"],
            "базовая версия АРМ оператора КПП"),
        new("2.5.0", 2, ["access_events.reader_id"],
            new() { ["002_add_reader_id.sql"] = "ALTER TABLE access_events ADD COLUMN reader_id TEXT" },
            ["canary", "stable"], "учёт идентификатора считывателя в журнале проходов"),
        new("2.5.1", 3, ["access_events.reader_id", "access_events.reader_zone"],
            // Ошибка разработчика: опечатка в имени колонки (readr_zone вместо reader_zone)
            new() { ["003_add_reader_zone.sql"] = "ALTER TABLE access_events ADD COLUMN readr_zone TEXT" },
            ["canary"], "зона прохода для считывателя (опытная эксплуатация)"),
        new("2.6.0", 3, ["access_events.reader_id"], new(), ["canary"],
            "новый интерфейс оператора (пакет повреждён при передаче)", Corrupt: true)
    ];

    /// <summary>Создать стенд. Возвращает путь к сгенерированному updater.json.</summary>
    public static string Create(string baseDir, string envName = "demo-env", bool force = false)
    {
        var envDir = Path.Combine(baseDir, envName);
        if (Directory.Exists(envDir))
        {
            if (!force)
                throw new IOException($"Каталог {envDir} уже существует. Используйте --force для пересоздания стенда.");
            SqliteConnection.ClearAllPools();
            Directory.Delete(envDir, recursive: true);
        }

        var layout = new ArmLayout(Path.Combine(envDir, "arm"));
        var server = Path.Combine(envDir, "update-server");
        Directory.CreateDirectory(Path.Combine(server, "packages"));

        // Сервер обновлений: пакеты + манифест
        var manifest = new ReleaseManifest();
        foreach (var spec in Packages)
        {
            var zip = BuildPackage(spec, Path.Combine(server, "packages"));
            var sha = Hashing.Sha256File(zip);
            if (spec.Corrupt)
            {
                // Имитация повреждения при передаче по сети: в конец архива дописаны лишние байты
                File.AppendAllText(zip, "\0\0CORRUPTED-IN-TRANSIT\0\0");
            }
            manifest.Releases.Add(new ReleaseInfo
            {
                Version = spec.Version,
                Rings = spec.Rings.ToList(),
                Package = $"packages/{Path.GetFileName(zip)}",
                Sha256 = sha,
                Notes = spec.Notes,
                Published = DateTime.Today.AddDays(-Packages.Length + Array.IndexOf(Packages, spec))
            });
        }
        File.WriteAllText(Path.Combine(server, "manifest.json"), JsonSerializer.Serialize(manifest, Json.Options));

        // АРМ: версия 2.4.0 в слоте blue
        Directory.CreateDirectory(layout.SlotsDir);
        ZipFile.ExtractToDirectory(Path.Combine(server, "packages", "Campus.Arm.Client_2.4.0.zip"), layout.SlotDir("blue"));
        layout.SwitchActiveSlot("blue");

        // Локальные данные АРМ
        Directory.CreateDirectory(layout.CacheDir);
        File.WriteAllText(layout.AppSettings, """
            {
              "ArmId": "ARM-KPP-01",
              "CentralApiUrl": "https://api.ipify.org?format=json",
              "ReaderMode": "Emulator",
              "Turnstile": { "Zone": "Главный вход, корпус А", "Direction": "IN/OUT" },
              "SyncIntervalSeconds": 30
            }
            """, Encoding.UTF8);
        File.WriteAllText(Path.Combine(layout.CacheDir, "operators.json"),
            """[{"login":"ivanova","name":"Иванова М. С."},{"login":"petrov","name":"Петров А. В."}]""", Encoding.UTF8);
        File.WriteAllText(Path.Combine(layout.CacheDir, "access-rules.json"),
            """{"students":"06:30-23:00","staff":"00:00-24:00","guests":"08:00-20:00"}""", Encoding.UTF8);
        CreateDatabase(layout.Database);

        Directory.CreateDirectory(Path.GetDirectoryName(layout.ReaderTestCard)!);
        File.WriteAllText(layout.ReaderTestCard, "04A1B2C3D4E5F6");

        // Настройки утилиты
        var configPath = Path.Combine(baseDir, "updater.json");
        File.WriteAllText(configPath, $$"""
            {
              "ArmId": "ARM-KPP-01",
              "Ring": "canary",
              "InstallRoot": "{{envName}}/arm",
              "UpdateSource": "{{envName}}/update-server",
              "BackupRoot": "{{envName}}/backups",
              "ReportsRoot": "{{envName}}/reports",
              "LogsRoot": "{{envName}}/logs",
              "BackupsToKeep": 5,
              "HealthCheck": {
                "ApiUrl": "https://api.ipify.org?format=json",
                "ApiTimeoutSeconds": 5,
                "ApiRetries": 3,
                "DatabaseWarnMs": 200,
                "ApiWarnMs": 1500,
                "ReaderWarnMs": 500,
                "Reader": { "Mode": "Emulator", "PortName": "COM3", "BaudRate": 9600, "TimeoutMs": 2000 }
              }
            }
            """, Encoding.UTF8);
        return configPath;
    }

    private static string BuildPackage(PackageSpec spec, string outDir)
    {
        var staging = Path.Combine(Path.GetTempPath(), $"campus-pkg-{spec.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(staging, "app"));
        try
        {
            // «Бинарники» клиента АРМ (на стенде — файлы-заглушки с уникальным содержимым версии)
            File.WriteAllText(Path.Combine(staging, "app", "Campus.Arm.Client.dll"),
                $"Campus.Arm.Client {spec.Version} {new string('#', 2048)}");
            File.WriteAllText(Path.Combine(staging, "app", "Campus.Arm.Client.exe"),
                $"MZ Campus.Arm.Client launcher {spec.Version}");
            File.WriteAllText(Path.Combine(staging, "app", "appsettings.default.json"),
                """{ "SyncIntervalSeconds": 30, "ReaderMode": "Emulator" }""");

            if (spec.Migrations.Count > 0)
            {
                Directory.CreateDirectory(Path.Combine(staging, "migrations"));
                foreach (var (name, sql) in spec.Migrations)
                    File.WriteAllText(Path.Combine(staging, "migrations", name), sql);
            }

            var info = new AppVersionInfo
            {
                Version = spec.Version,
                SchemaVersion = spec.Schema,
                RequiredColumns = spec.RequiredColumns.ToList(),
                BuildDate = DateTime.Today
            };
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                info.Files[Path.GetRelativePath(staging, file).Replace('\\', '/')] = Hashing.Sha256File(file);
            File.WriteAllText(Path.Combine(staging, "version.json"), JsonSerializer.Serialize(info, Json.Options));

            var zip = Path.Combine(outDir, $"Campus.Arm.Client_{spec.Version}.zip");
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(staging, zip);
            return zip;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private static void CreateDatabase(string path)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE schema_info (version INTEGER NOT NULL);
            INSERT INTO schema_info (version) VALUES (1);
            CREATE TABLE operators (
                id INTEGER PRIMARY KEY,
                login TEXT NOT NULL UNIQUE,
                full_name TEXT NOT NULL,
                role TEXT NOT NULL);
            CREATE TABLE access_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                card_uid TEXT NOT NULL,
                direction TEXT NOT NULL CHECK (direction IN ('IN', 'OUT')),
                event_time TEXT NOT NULL);
            CREATE TABLE sync_queue (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                payload TEXT NOT NULL,
                created_at TEXT NOT NULL);
            INSERT INTO operators (login, full_name, role) VALUES
                ('ivanova', 'Иванова Мария Сергеевна', 'operator'),
                ('petrov', 'Петров Алексей Викторович', 'operator'),
                ('admin', 'Администратор АРМ', 'admin');
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1200)
            INSERT INTO access_events (card_uid, direction, event_time)
            SELECT printf('04%012X', 1000000 + i * 37), CASE i % 2 WHEN 0 THEN 'IN' ELSE 'OUT' END,
                   datetime('now', printf('-%d minutes', i * 7)) FROM n;
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 25)
            INSERT INTO sync_queue (payload, created_at)
            SELECT printf('{"eventId":%d}', 1200 - i), datetime('now', printf('-%d minutes', i)) FROM n;
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
}
