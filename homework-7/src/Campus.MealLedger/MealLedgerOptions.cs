using System.Text.Json;

namespace Campus.MealLedger;

/// <summary>
/// Настройки утилиты. Порядок приоритета (от низшего к высшему):
/// appsettings.json -> переменные окружения CAMPUS_MEAL_* -> аргументы командной строки.
/// Ключ доступа к API читается ТОЛЬКО из переменной окружения CAMPUS_API_KEY
/// и никогда не хранится в appsettings.json.
/// </summary>
public sealed class MealLedgerOptions
{
    public string SourceUrl { get; set; } = "https://jsonplaceholder.typicode.com/posts";
    public string OutputDirectory { get; set; } = "output";
    public string LogDirectory { get; set; } = "logs";
    public string CanteenName { get; set; } = "Столовая";
    public int TimeoutSeconds { get; set; } = 15;
    public int RetryCount { get; set; } = 3;
    public Dictionary<string, decimal> Prices { get; set; } = new();
    public List<string> SubsidizedMeals { get; set; } = new();

    // Не сериализуется в файл, заполняется из окружения
    public string? ApiKey { get; private set; }

    public DateOnly ReportDate { get; set; } = DateOnly.FromDateTime(DateTime.Now);

    public static MealLedgerOptions Load(string[] args)
    {
        var options = new MealLedgerOptions();

        // 1. Файл конфигурации рядом с exe
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("MealLedger", out var section))
            {
                var fromFile = section.Deserialize<MealLedgerOptions>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (fromFile is not null)
                    options = fromFile;
            }
        }

        // 2. Переменные окружения (перекрывают файл)
        options.SourceUrl = Env("CAMPUS_MEAL_SOURCE_URL") ?? options.SourceUrl;
        options.OutputDirectory = Env("CAMPUS_MEAL_OUTPUT_DIR") ?? options.OutputDirectory;
        options.ApiKey = Env("CAMPUS_API_KEY");

        // 3. Аргументы командной строки: --date 2026-10-27 --out D:\reports
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--date":
                    options.ReportDate = DateOnly.ParseExact(args[i + 1], "yyyy-MM-dd");
                    break;
                case "--out":
                    options.OutputDirectory = args[i + 1];
                    break;
            }
        }

        options.Validate();
        return options;
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private void Validate()
    {
        if (!Uri.TryCreate(SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"SourceUrl должен быть HTTPS-адресом (порт 443): {SourceUrl}");

        foreach (var meal in TransactionSimulator.MealTypes)
        {
            if (!Prices.TryGetValue(meal, out var price) || price <= 0)
                throw new InvalidOperationException($"В разделе Prices не задана цена для категории «{meal}»");
        }

        if (TimeoutSeconds is < 1 or > 120)
            throw new InvalidOperationException("TimeoutSeconds должен быть в диапазоне 1..120");
    }

    /// <summary>Маскированное значение ключа для журнала: показываются только последние 4 символа.</summary>
    public string MaskedApiKey =>
        ApiKey is null ? "не задан" : $"****{ApiKey[^Math.Min(4, ApiKey.Length)..]}";
}
