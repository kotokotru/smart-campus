using System.Globalization;
using System.Text;
using Campus.Arm.Updater.Health;
using Campus.Arm.Updater.Infrastructure;

namespace Campus.Arm.Updater.Reporting;

/// <summary>Данные для акта готовности АРМ.</summary>
public sealed class ReadinessReport
{
    public required string ArmId { get; init; }
    public required string Operation { get; init; }
    public string VersionBefore { get; init; } = "—";
    public string VersionAfter { get; init; } = "—";
    public string ActiveSlot { get; init; } = "—";
    public string Ring { get; init; } = "—";
    public string? BackupPath { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; init; } = DateTime.Now;
    public required IReadOnlyList<HealthCheckResult> Results { get; init; }
    public List<string> Notes { get; init; } = [];

    /// <summary>АРМ готов, если не провалена ни одна критичная проверка и не было отката.</summary>
    public bool RolledBack { get; init; }
    public bool Ready => !RolledBack && Results.Count > 0 && Results.All(r => r.Status != HealthStatus.Fail || !r.Critical);
}

/// <summary>
/// Формирование акта готовности АРМ к промышленной эксплуатации (Markdown) —
/// документ остаётся в каталоге reports/ и прикладывается к заявке на изменение.
/// </summary>
public static class ReadinessAct
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Write(ReadinessReport r, string reportsDir)
    {
        Directory.CreateDirectory(reportsDir);
        var file = Path.Combine(reportsDir, $"ACT_{r.ArmId}_{r.FinishedAt:yyyyMMdd_HHmmss_fff}.md");

        var sb = new StringBuilder();
        sb.AppendLine("# АКТ готовности АРМ к промышленной эксплуатации");
        sb.AppendLine();
        sb.AppendLine("ИС «Умный кампус» · клиентский модуль «АРМ оператора»");
        sb.AppendLine();
        sb.AppendLine("| Параметр | Значение |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| АРМ | {r.ArmId} ({Environment.MachineName}) |");
        sb.AppendLine($"| Операция | {r.Operation} |");
        sb.AppendLine($"| Версия до / после | {r.VersionBefore} / {r.VersionAfter} |");
        sb.AppendLine($"| Активный слот (Blue-Green) | {r.ActiveSlot} |");
        sb.AppendLine($"| Кольцо развёртывания | {r.Ring} |");
        sb.AppendLine($"| Резервная копия | {(r.BackupPath is null ? "не создавалась" : Paths.Rel(r.BackupPath))} |");
        sb.AppendLine($"| Начало / окончание | {r.StartedAt:dd.MM.yyyy HH:mm:ss} / {r.FinishedAt:dd.MM.yyyy HH:mm:ss} |");
        sb.AppendLine();

        sb.AppendLine("## Результаты проверок (Health Check)");
        sb.AppendLine();
        sb.AppendLine("| № | Проверка | Критичная | Статус | Задержка, мс | Результат |");
        sb.AppendLine("|---|---|---|---|---|---|");
        var i = 1;
        foreach (var x in r.Results)
            sb.AppendLine($"| {i++} | {x.Name} | {(x.Critical ? "да" : "нет")} | {x.StatusText} | {x.LatencyMs.ToString("0.0", Ru)} | {x.Details.Replace("|", "/")} |");
        sb.AppendLine();

        if (r.Results.Count > 0)
        {
            var lat = r.Results.Select(x => x.LatencyMs).ToList();
            sb.AppendLine($"Задержка проверок: мин. {lat.Min().ToString("0.0", Ru)} мс, " +
                          $"средн. {lat.Average().ToString("0.0", Ru)} мс, макс. {lat.Max().ToString("0.0", Ru)} мс, " +
                          $"суммарно {lat.Sum().ToString("0.0", Ru)} мс.");
            sb.AppendLine();
        }

        if (r.Notes.Count > 0)
        {
            sb.AppendLine("## Ход выполнения");
            sb.AppendLine();
            foreach (var n in r.Notes) sb.AppendLine($"- {n}");
            sb.AppendLine();
        }

        sb.AppendLine("## Заключение");
        sb.AppendLine();
        sb.AppendLine(r.Ready
            ? $"**АРМ {r.ArmId} ГОТОВ к промышленной эксплуатации** с версией {r.VersionAfter}."
            : r.RolledBack
                ? $"**АРМ {r.ArmId} НЕ ГОТОВ к эксплуатации версии {r.VersionAfter}.** Выполнен откат на версию {r.VersionBefore}; работа АРМ продолжается на предыдущей версии."
                : $"**АРМ {r.ArmId} НЕ ГОТОВ к промышленной эксплуатации.** Требуется устранение замечаний.");
        sb.AppendLine();
        sb.AppendLine("Системный администратор: ____________________   Оператор АРМ: ____________________");

        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return file;
    }

    /// <summary>Сводная таблица в консоль.</summary>
    public static void PrintSummary(ReadinessReport r)
    {
        Console.WriteLine();
        Console.WriteLine(new string('═', 78));
        Console.WriteLine($" АКТ ГОТОВНОСТИ АРМ {r.ArmId} · {r.Operation}");
        Console.WriteLine(new string('─', 78));
        Console.WriteLine($" {"Проверка",-46} {"Статус",-15} {"Задержка, мс",12}");
        foreach (var x in r.Results)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = x.Status switch
            {
                HealthStatus.Pass => ConsoleColor.Green,
                HealthStatus.Warn => ConsoleColor.Yellow,
                _ => ConsoleColor.Red
            };
            Console.WriteLine($" {x.Name,-46} {x.StatusText,-15} {x.LatencyMs,12:0.0}");
            Console.ForegroundColor = old;
        }
        Console.WriteLine(new string('─', 78));
        var c = Console.ForegroundColor;
        Console.ForegroundColor = r.Ready ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine(r.Ready
            ? $" ИТОГ: АРМ ГОТОВ к промышленной эксплуатации (версия {r.VersionAfter})"
            : r.RolledBack
                ? $" ИТОГ: АРМ НЕ ГОТОВ к версии {r.VersionAfter} — выполнен ОТКАТ на {r.VersionBefore}"
                : " ИТОГ: АРМ НЕ ГОТОВ — см. акт и журнал");
        Console.ForegroundColor = c;
        Console.WriteLine(new string('═', 78));
    }
}
