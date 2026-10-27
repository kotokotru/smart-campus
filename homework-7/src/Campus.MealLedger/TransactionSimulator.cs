namespace Campus.MealLedger;

/// <summary>
/// Превращает записи публичного API в транзакции питания.
/// Преобразование детерминированное: одни и те же данные API всегда дают
/// одну и ту же ведомость, поэтому результат можно перепроверить.
/// </summary>
public static class TransactionSimulator
{
    public static readonly string[] MealTypes = { "Завтрак", "Обед", "Полдник", "Ужин", "Буфет" };

    // Время начала раздачи для каждой категории
    private static readonly Dictionary<string, TimeSpan> ServingStart = new()
    {
        ["Завтрак"] = new TimeSpan(8, 0, 0),
        ["Обед"] = new TimeSpan(12, 30, 0),
        ["Полдник"] = new TimeSpan(15, 30, 0),
        ["Ужин"] = new TimeSpan(18, 0, 0),
        ["Буфет"] = new TimeSpan(10, 0, 0)
    };

    public static List<MealTransaction> Simulate(IEnumerable<SourcePost> posts, MealLedgerOptions options)
    {
        var result = new List<MealTransaction>();
        var date = options.ReportDate.ToDateTime(TimeOnly.MinValue);

        foreach (var post in posts.OrderBy(p => p.Id))
        {
            // Категория питания зависит от номера записи
            var mealType = MealTypes[(post.Id - 1) % MealTypes.Length];

            // userId 1..10 -> номер карты посетителя и его категория
            var payer = PayerCategoryOf(post.UserId);

            // Цена = базовая цена категории + доп. позиции (0..60 руб.), зависящие от длины заголовка
            var extra = post.Title.Length % 7 * 10m;
            var amount = options.Prices[mealType] + extra;

            // Льготникам дотация покрывает базовую цену льготных категорий, допы оплачиваются сами
            var subsidy = payer == "Льготник" && options.SubsidizedMeals.Contains(mealType)
                ? options.Prices[mealType]
                : 0m;

            var time = date + ServingStart[mealType] + TimeSpan.FromMinutes(post.Id * 7 % 90);

            result.Add(new MealTransaction(
                TransactionId: $"MEAL-{options.ReportDate:yyyyMMdd}-{post.Id:D4}",
                Timestamp: time,
                Terminal: $"POS-{post.Id % 3 + 1:D2}",
                CardId: $"CARD-{post.UserId:D4}",
                PayerCategory: payer,
                MealType: mealType,
                Amount: amount,
                Subsidy: subsidy,
                SourceRef: $"posts/{post.Id}"));
        }

        return result;
    }

    public static string PayerCategoryOf(int userId) => userId switch
    {
        <= 3 => "Льготник",
        <= 8 => "Студент",
        _ => "Сотрудник"
    };

    /// <summary>Группировка транзакций по категориям питания (итоговая ведомость).</summary>
    public static List<LedgerRow> GroupByMealType(IReadOnlyCollection<MealTransaction> transactions)
    {
        return transactions
            .GroupBy(t => t.MealType)
            .OrderBy(g => Array.IndexOf(MealTypes, g.Key))
            .Select(g => new LedgerRow(
                MealType: g.Key,
                Count: g.Count(),
                Visitors: g.Select(t => t.CardId).Distinct().Count(),
                Amount: g.Sum(t => t.Amount),
                Subsidy: g.Sum(t => t.Subsidy),
                ToPay: g.Sum(t => t.ToPay),
                AverageCheck: Math.Round(g.Average(t => t.Amount), 2)))
            .ToList();
    }
}
