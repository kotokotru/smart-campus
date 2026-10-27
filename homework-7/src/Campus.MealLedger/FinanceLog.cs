using System.Text;

namespace Campus.MealLedger;

/// <summary>
/// Финансовый лог: построчный журнал работы утилиты (logs/meal-ledger-ГГГГ-ММ.log).
/// Файл только дополняется, существующие строки не перезаписываются.
/// </summary>
public sealed class FinanceLog
{
    private readonly string _path;

    public FinanceLog(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"meal-ledger-{DateTime.Now:yyyy-MM}.log");
    }

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {Environment.MachineName} {message}";
        Console.WriteLine(line);
        File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
    }
}
