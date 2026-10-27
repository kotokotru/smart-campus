namespace Campus.MealLedger;

/// <summary>Запись из публичного API jsonplaceholder (/posts) — используется как «сырая» выгрузка кассы.</summary>
public sealed record SourcePost(int UserId, int Id, string Title, string Body);

/// <summary>Одна транзакция питания в столовой (строка детального журнала).</summary>
public sealed record MealTransaction(
    string TransactionId,
    DateTime Timestamp,
    string Terminal,
    string CardId,
    string PayerCategory,
    string MealType,
    decimal Amount,
    decimal Subsidy,
    string SourceRef)
{
    /// <summary>Сумма к оплате самим посетителем.</summary>
    public decimal ToPay => Amount - Subsidy;
}

/// <summary>Итоговая строка ведомости по одной категории питания.</summary>
public sealed record LedgerRow(
    string MealType,
    int Count,
    int Visitors,
    decimal Amount,
    decimal Subsidy,
    decimal ToPay,
    decimal AverageCheck);
