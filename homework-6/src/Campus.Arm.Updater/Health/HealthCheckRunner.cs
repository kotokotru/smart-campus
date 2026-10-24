using System.Diagnostics;
using Campus.Arm.Updater.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Campus.Arm.Updater.Health;

/// <summary>
/// Выполняет цепочку проверок по порядку (целостность → БД → API → считыватель),
/// измеряет задержку (Latency) каждой проверки и пишет её в журнал.
/// Любое исключение внутри проверки превращается в результат «НЕ ПРОЙДЕНА»,
/// поэтому один сбой не обрывает всю цепочку и акт готовности всегда формируется.
/// </summary>
public sealed class HealthCheckRunner(IReadOnlyList<IHealthCheck> checks)
{
    public async Task<IReadOnlyList<HealthCheckResult>> RunAsync(HealthContext context, CancellationToken ct = default)
    {
        var log = context.Log;
        var results = new List<HealthCheckResult>();
        log.Step($"[HEALTH] Запуск цепочки проверок ({checks.Count}): {string.Join(" → ", checks.Select(c => c.Code))}");

        foreach (var check in checks)
        {
            var sw = Stopwatch.StartNew();
            HealthStatus status;
            string details;
            try
            {
                details = await check.RunAsync(context, ct);
                status = HealthStatus.Pass;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // прервано пользователем (Ctrl+C)
            }
            catch (Exception ex)
            {
                status = HealthStatus.Fail;
                details = Describe(ex);
            }
            sw.Stop();

            var latency = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
            var warnMs = check.WarnLatencyMs(context.Options.HealthCheck);
            if (status == HealthStatus.Pass && warnMs > 0 && latency > warnMs)
            {
                status = HealthStatus.Warn;
                details += $" (задержка {latency} мс > порога {warnMs} мс)";
            }

            var result = new HealthCheckResult(check.Code, check.Name, status, latency, details, check.Critical);
            results.Add(result);

            // Строка 1 — код, статус и задержка; строка 2 — подробности проверки
            Action<string> write = status switch
            {
                HealthStatus.Pass => log.Ok,
                HealthStatus.Warn => log.Warn,
                _ => log.Error
            };
            write($"[HEALTH] {check.Code,-9} {result.StatusText,-14} latency={latency,8:0.0} мс");
            write($"[HEALTH]   └─ {details}");
        }
        return results;
    }

    /// <summary>Понятное описание типовых исключений для журнала и акта.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        FileNotFoundException f => $"файл не найден: {f.FileName ?? f.Message}",
        UnauthorizedAccessException u => $"нет доступа: {u.Message}",
        TimeoutException t => $"таймаут: {t.Message}",
        SqliteException s => $"ошибка SQLite {s.SqliteErrorCode}: {s.Message}",
        HttpRequestException h => $"сетевая ошибка: {h.Message}",
        IOException io => $"ошибка ввода-вывода: {io.Message}",
        _ => ex.Message
    };
}
