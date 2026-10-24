using Campus.Arm.Updater.Config;
using Campus.Arm.Updater.Infrastructure;
using Campus.Arm.Updater.Update;

namespace Campus.Arm.Updater.Health;

public enum HealthStatus
{
    Pass,   // проверка пройдена
    Warn,   // пройдена, но задержка выше порога
    Fail    // не пройдена
}

/// <summary>Результат одной проверки цепочки Health Check.</summary>
public sealed record HealthCheckResult(
    string Code,
    string Name,
    HealthStatus Status,
    double LatencyMs,
    string Details,
    bool Critical)
{
    public string StatusText => Status switch
    {
        HealthStatus.Pass => "ПРОЙДЕНА",
        HealthStatus.Warn => "ПРЕДУПРЕЖДЕНИЕ",
        _ => "НЕ ПРОЙДЕНА"
    };
}

/// <summary>Данные, общие для всех проверок одного прогона.</summary>
public sealed class HealthContext
{
    public required ArmLayout Layout { get; init; }
    public required UpdaterOptions Options { get; init; }
    public required AppLogger Log { get; init; }

    /// <summary>Количество строк в ключевых таблицах до обновления (для проверки сохранности данных).</summary>
    public IReadOnlyDictionary<string, long>? RowsBefore { get; init; }
}

/// <summary>Одна проверка в цепочке.</summary>
public interface IHealthCheck
{
    /// <summary>Короткий код для журнала: INTEGRITY, DB, API, READER.</summary>
    string Code { get; }
    string Name { get; }

    /// <summary>Критичная проверка: её провал после обновления запускает откат.</summary>
    bool Critical { get; }

    /// <summary>Порог задержки, мс (0 — без порога).</summary>
    int WarnLatencyMs(HealthCheckOptions options);

    /// <summary>Выполнить проверку. Возвращает текст результата; при ошибке бросает исключение
    /// (его перехватывает <see cref="HealthCheckRunner"/> и превращает в статус «НЕ ПРОЙДЕНА»).</summary>
    Task<string> RunAsync(HealthContext context, CancellationToken ct);
}
