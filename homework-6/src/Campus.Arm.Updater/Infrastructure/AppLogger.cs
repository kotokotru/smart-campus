namespace Campus.Arm.Updater.Infrastructure;

/// <summary>
/// Журнал утилиты: цветной вывод в консоль + файл logs/updater-ГГГГММДД.log.
/// Ошибка записи в файл (диск заполнен, нет прав) не прерывает обновление —
/// сообщение остаётся в консоли.
/// </summary>
public sealed class AppLogger
{
    private readonly object _gate = new();
    private string? _file;

    /// <summary>Не писать в консоль (для автотестов).</summary>
    public bool Quiet { get; init; }

    public string? FilePath => _file;

    public AppLogger(string? logsDir)
    {
        if (logsDir is null) return;
        try
        {
            Directory.CreateDirectory(logsDir);
            _file = Path.Combine(logsDir, $"updater-{DateTime.Now:yyyyMMdd}.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[WRN] Каталог журнала недоступен ({ex.Message}); журнал только в консоли.");
        }
    }

    public void Info(string message) => Write("INF", message, ConsoleColor.Gray);
    public void Step(string message) => Write("INF", message, ConsoleColor.Cyan);
    public void Ok(string message) => Write("INF", message, ConsoleColor.Green);
    public void Warn(string message) => Write("WRN", message, ConsoleColor.Yellow);
    public void Error(string message) => Write("ERR", message, ConsoleColor.Red);

    private void Write(string level, string message, ConsoleColor color)
    {
        var now = DateTime.Now;
        lock (_gate)
        {
            if (!Quiet)
            {
                var old = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine($"{now:HH:mm:ss.fff} [{level}] {message}");
                Console.ForegroundColor = old;
            }

            if (_file is null) return;
            try
            {
                File.AppendAllText(_file, $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Файл журнала занят или диск недоступен — дальше пишем только в консоль.
                _file = null;
                if (!Quiet) Console.Error.WriteLine($"[WRN] Запись в журнал прекращена: {ex.Message}");
            }
        }
    }
}
