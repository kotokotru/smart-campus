# ПР7 — Внедрение клиентского ПО на АРМ. Вариант 10

Генератор ведомости питания (финансовый лог) для ИС «Умный кампус».

| Этап | Файл |
|---|---|
| 1. Сценарий внедрения, чек-лист аудита АРМ, правила передачи ключей | [docs/DEPLOYMENT_SCENARIO.md](docs/DEPLOYMENT_SCENARIO.md) |
| 2. Утилита C# (.NET 8) | [src/Campus.MealLedger](src/Campus.MealLedger) |
| 3. Руководство оператора | [OPERATOR_MANUAL.md](OPERATOR_MANUAL.md) |
| 4. Отчётные формы и журналы | [docs/REPORT_FORMS.md](docs/REPORT_FORMS.md) |
| Пример результата | [output](output) |

## Запуск

```powershell
cd src/Campus.MealLedger
dotnet run -- --date 2026-10-27 --out ../../output
```

Источник данных — `https://jsonplaceholder.typicode.com/posts`. Записи API превращаются в транзакции питания, группируются по категориям (завтрак, обед, полдник, ужин, буфет) и выгружаются в CSV с файлом контрольных сумм SHA-256.
