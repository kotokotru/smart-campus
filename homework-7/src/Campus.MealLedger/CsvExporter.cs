using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Campus.MealLedger;

/// <summary>
/// Экспорт в CSV для Excel (русская локаль): разделитель «;», десятичная запятая,
/// кодировка UTF-8 с BOM, чтобы кириллица открывалась без «кракозябр».
/// </summary>
public static class CsvExporter
{
    private const char Separator = ';';
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static string WriteTransactions(string directory, DateOnly date, IEnumerable<MealTransaction> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Join("ID транзакции", "Дата и время", "Терминал", "Карта", "Категория посетителя",
            "Категория питания", "Сумма, руб.", "Дотация, руб.", "К оплате, руб.", "Источник"));

        foreach (var t in rows)
        {
            sb.AppendLine(Join(t.TransactionId, t.Timestamp.ToString("dd.MM.yyyy HH:mm", Ru), t.Terminal,
                t.CardId, t.PayerCategory, t.MealType, Money(t.Amount), Money(t.Subsidy), Money(t.ToPay),
                t.SourceRef));
        }

        return Save(directory, $"meal_transactions_{date:yyyy-MM-dd}.csv", sb);
    }

    public static string WriteLedger(string directory, DateOnly date, string canteen, IReadOnlyList<LedgerRow> rows)
    {
        var sb = new StringBuilder();
        // Шапка ведомости — служебные строки перед таблицей
        sb.AppendLine(Join("Ведомость учета питания", canteen));
        sb.AppendLine(Join("Дата", date.ToString("dd.MM.yyyy", Ru)));
        sb.AppendLine(Join("Сформировано", DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss", Ru)));
        sb.AppendLine();
        sb.AppendLine(Join("Категория питания", "Транзакций", "Посетителей", "Сумма, руб.",
            "Дотация, руб.", "К оплате, руб.", "Средний чек, руб."));

        foreach (var r in rows)
        {
            sb.AppendLine(Join(r.MealType, r.Count.ToString(), r.Visitors.ToString(), Money(r.Amount),
                Money(r.Subsidy), Money(r.ToPay), Money(r.AverageCheck)));
        }

        sb.AppendLine(Join("ИТОГО", rows.Sum(r => r.Count).ToString(), "",
            Money(rows.Sum(r => r.Amount)), Money(rows.Sum(r => r.Subsidy)), Money(rows.Sum(r => r.ToPay)), ""));

        return Save(directory, $"meal_ledger_{date:yyyy-MM-dd}.csv", sb);
    }

    /// <summary>
    /// Защита от изменений: файл контрольных сумм SHA-256 в формате утилиты sha256sum.
    /// Любая правка CSV после выгрузки обнаруживается сверкой хеша.
    /// </summary>
    public static string WriteChecksums(string directory, DateOnly date, params string[] files)
    {
        var sb = new StringBuilder();
        foreach (var file in files)
        {
            using var stream = File.OpenRead(file);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            sb.Append(hash).Append("  ").Append(Path.GetFileName(file)).Append('\n');
        }

        var path = Path.Combine(directory, $"meal_ledger_{date:yyyy-MM-dd}.sha256");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string Save(string directory, string fileName, StringBuilder content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content.ToString(), Utf8Bom);
        return path;
    }

    private static string Money(decimal value) => value.ToString("0.00", Ru);

    private static string Join(params string[] fields) => string.Join(Separator, fields.Select(Escape));

    // Экранирование по RFC 4180: поле в кавычках, если в нём есть разделитель, кавычка или перевод строки
    private static string Escape(string field)
    {
        if (field.IndexOfAny(new[] { Separator, '"', '\n', '\r' }) < 0)
            return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
