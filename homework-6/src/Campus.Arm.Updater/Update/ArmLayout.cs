using System.Text.Json;
using System.Text.Json.Serialization;
using Campus.Arm.Updater.Infrastructure;

namespace Campus.Arm.Updater.Update;

/// <summary>
/// Структура каталога клиента АРМ «Умный кампус» (схема Blue-Green):
/// <code>
/// InstallRoot/
///   slots/blue/            ← версия N   (приложение, version.json)
///   slots/green/           ← версия N+1 (ставится «рядом», пока работает N)
///   active-slot.txt        ← какой слот сейчас запускается ("blue"/"green")
///   data/appsettings.json  ← локальные настройки АРМ
///   data/cache/            ← кэш справочников
///   data/campus-local.db   ← локальная БД SQLite (журнал проходов, очередь синхронизации)
/// </code>
/// Переключение версии = атомарная замена active-slot.txt, откат = переключение обратно.
/// </summary>
public sealed class ArmLayout(string root)
{
    public string Root { get; } = root;
    public string SlotsDir => Path.Combine(Root, "slots");
    public string ActiveSlotFile => Path.Combine(Root, "active-slot.txt");
    public string DataDir => Path.Combine(Root, "data");
    public string AppSettings => Path.Combine(DataDir, "appsettings.json");
    public string CacheDir => Path.Combine(DataDir, "cache");
    public string Database => Path.Combine(DataDir, "campus-local.db");
    public string DownloadsDir => Path.Combine(Root, "downloads");
    public string ReaderTestCard => Path.Combine(Root, "reader", "test-card.txt");

    public string SlotDir(string slot) => Path.Combine(SlotsDir, slot);

    public static string OtherSlot(string slot) => slot == "blue" ? "green" : "blue";

    public string ReadActiveSlot()
    {
        if (!File.Exists(ActiveSlotFile)) return "blue";
        var slot = File.ReadAllText(ActiveSlotFile).Trim();
        return slot is "blue" or "green"
            ? slot
            : throw new InvalidDataException($"Повреждён файл {ActiveSlotFile}: '{slot}'");
    }

    /// <summary>Атомарное переключение слота: запись во временный файл и замена.</summary>
    public void SwitchActiveSlot(string slot)
    {
        var tmp = ActiveSlotFile + ".tmp";
        File.WriteAllText(tmp, slot);
        File.Move(tmp, ActiveSlotFile, overwrite: true);
    }

    /// <summary>Чтение version.json из слота (null — слот пуст).</summary>
    public AppVersionInfo? ReadVersion(string slot)
    {
        var file = Path.Combine(SlotDir(slot), "version.json");
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize<AppVersionInfo>(File.ReadAllText(file), Json.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Повреждён {file}: {ex.Message}", ex);
        }
    }
}

/// <summary>Описание версии клиента АРМ (version.json внутри пакета обновления).</summary>
public sealed class AppVersionInfo
{
    public string Version { get; set; } = "0.0.0";

    /// <summary>Версия схемы локальной БД, которую ожидает эта версия клиента.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Колонки, без которых клиент не работает (формат "таблица.колонка").</summary>
    public List<string> RequiredColumns { get; set; } = [];

    /// <summary>Контрольные суммы файлов пакета (путь → SHA-256).</summary>
    public Dictionary<string, string> Files { get; set; } = new();

    public DateTime BuildDate { get; set; }
}

/// <summary>Манифест сервера обновлений (manifest.json в папке UpdateSource).</summary>
public sealed class ReleaseManifest
{
    public string Product { get; set; } = "Campus.Arm.Client";
    public List<ReleaseInfo> Releases { get; set; } = [];
}

public sealed class ReleaseInfo
{
    public string Version { get; set; } = "";

    /// <summary>Кольца, которым доступен релиз: ["canary"] — опытная эксплуатация, ["canary","stable"] — промышленная.</summary>
    public List<string> Rings { get; set; } = [];

    /// <summary>Путь к zip-пакету относительно папки сервера обновлений.</summary>
    public string Package { get; set; } = "";

    /// <summary>SHA-256 zip-пакета — проверяется до установки.</summary>
    public string Sha256 { get; set; } = "";

    public string Notes { get; set; } = "";
    public DateTime Published { get; set; }

    [JsonIgnore]
    public System.Version ParsedVersion => System.Version.Parse(Version);
}
