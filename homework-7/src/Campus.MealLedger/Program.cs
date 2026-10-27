using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Campus.MealLedger;

// Генератор ведомости питания (вариант 10, ПР7 МДК 03.01, ИС «Умный кампус»).
// Коды возврата: 0 — успех, 1 — ошибка конфигурации, 2 — источник недоступен, 3 — ошибка контроля данных.

Console.OutputEncoding = Encoding.UTF8;

MealLedgerOptions options;
try
{
    options = MealLedgerOptions.Load(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Ошибка конфигурации: {ex.Message}");
    return 1;
}

var log = new FinanceLog(options.LogDirectory);
log.Info($"Запуск Campus.MealLedger 1.0.0, .NET {Environment.Version}, дата ведомости {options.ReportDate:dd.MM.yyyy}");
log.Info($"Источник: {options.SourceUrl}; ключ API: {options.MaskedApiKey}");

// Шаг 1. Выгрузка «сырых» данных из публичного API
List<SourcePost> posts;
try
{
    posts = await DownloadAsync(options, log);
}
catch (Exception ex)
{
    log.Error($"Источник недоступен после {options.RetryCount} попыток: {ex.Message}");
    return 2;
}

// Шаг 2. Входной контроль: пустая выгрузка или повторяющиеся ID недопустимы
if (posts.Count == 0 || posts.Select(p => p.Id).Distinct().Count() != posts.Count)
{
    log.Error($"Входной контроль не пройден: записей {posts.Count}, есть дубликаты ID");
    return 3;
}
log.Info($"Получено записей: {posts.Count}");

// Шаг 3. Симуляция транзакций и группировка по категориям
var transactions = TransactionSimulator.Simulate(posts, options);
var ledger = TransactionSimulator.GroupByMealType(transactions);

// Шаг 4. Контроль полноты: итог ведомости должен совпасть с суммой детального журнала
var detailTotal = transactions.Sum(t => t.Amount);
var ledgerTotal = ledger.Sum(r => r.Amount);
if (detailTotal != ledgerTotal || ledger.Sum(r => r.Count) != transactions.Count)
{
    log.Error($"Расхождение итогов: журнал {detailTotal:0.00}, ведомость {ledgerTotal:0.00}");
    return 3;
}

// Шаг 5. Экспорт в CSV и фиксация контрольных сумм
var outDir = Path.GetFullPath(options.OutputDirectory);
var detailFile = CsvExporter.WriteTransactions(outDir, options.ReportDate, transactions);
var ledgerFile = CsvExporter.WriteLedger(outDir, options.ReportDate, options.CanteenName, ledger);
var hashFile = CsvExporter.WriteChecksums(outDir, options.ReportDate, detailFile, ledgerFile);

PrintLedger(ledger);

log.Info($"Журнал транзакций: {detailFile}");
log.Info($"Ведомость:         {ledgerFile}");
log.Info($"Контрольные суммы: {hashFile}");
log.Info($"Итого: {transactions.Count} транзакций на {detailTotal:0.00} руб., " +
         $"дотация {ledger.Sum(r => r.Subsidy):0.00} руб.");
return 0;

static async Task<List<SourcePost>> DownloadAsync(MealLedgerOptions options, FinanceLog log)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Campus.MealLedger/1.0");
    if (options.ApiKey is not null)
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

    for (var attempt = 1; ; attempt++)
    {
        try
        {
            var data = await http.GetFromJsonAsync<List<SourcePost>>(options.SourceUrl);
            return data ?? new List<SourcePost>();
        }
        catch (Exception ex) when (attempt < options.RetryCount && ex is HttpRequestException or TaskCanceledException)
        {
            // Пауза растёт с каждой попыткой: 2, 4, 6 секунд
            log.Warn($"Попытка {attempt} не удалась ({ex.Message}), повтор через {attempt * 2} с");
            await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
        }
    }
}

static void PrintLedger(IReadOnlyList<LedgerRow> ledger)
{
    Console.WriteLine();
    Console.WriteLine($"{"Категория",-10} {"Транз.",6} {"Посет.",6} {"Сумма",10} {"Дотация",10} {"К оплате",10}");
    foreach (var r in ledger)
        Console.WriteLine($"{r.MealType,-10} {r.Count,6} {r.Visitors,6} {r.Amount,10:0.00} {r.Subsidy,10:0.00} {r.ToPay,10:0.00}");
    Console.WriteLine($"{"ИТОГО",-10} {ledger.Sum(r => r.Count),6} {"",6} {ledger.Sum(r => r.Amount),10:0.00} " +
                      $"{ledger.Sum(r => r.Subsidy),10:0.00} {ledger.Sum(r => r.ToPay),10:0.00}");
    Console.WriteLine();
}
