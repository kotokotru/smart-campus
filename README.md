# ИС «Умный кампус» — практические работы МДК 03.01 и МДК 03.02

Леонов Тимофей Андреевич, группа И14-23-9

| Папка | Работа |
|---|---|
| [`homework-5/`](homework-5/) | Безопасный релиз, миграция Expand and Contract и Docs-as-Code. Вариант 10 — «Спортивный комплекс», CAMPUS-110 (`TimeSlot` → `TimeRange`). Отчёт: [`homework-5/REPORT.md`](homework-5/REPORT.md) |
| [`homework-6/`](homework-6/) | CALS на этапе эксплуатации: регламент обновления АРМ оператора (Canary / Blue-Green, резервное копирование, откат), утилита C# «Тестер Health Check после обновления», Docs-as-Code. Описание: [`homework-6/README.md`](homework-6/README.md) |
| [`mdk-03-02-pr6/`](mdk-03-02-pr6/) | МДК 03.02, ПР6: комплексное тестирование и отчёт о тестировании (ISO/IEC 25010). Вариант 10 — «Общежитие и заселение» (`DormitoryCheckIn`), ASP.NET Core + xUnit + SQLite In-Memory. Отчёт: [`mdk-03-02-pr6/REPORT.md`](mdk-03-02-pr6/REPORT.md) |

## Быстрый старт (homework-5)

```bash
cd homework-5
dotnet test Campus.Sports.sln                         # unit + tracing + sanity (in-process)

# миграции на локальном PostgreSQL 16
psql -v ON_ERROR_STOP=1 -f db/migrations/00_baseline_v1.8.sql
psql -v ON_ERROR_STOP=1 -f db/migrations/01_expand.sql
psql -v ON_ERROR_STOP=1 -f db/migrations/02_migrate.sql
psql -v ON_ERROR_STOP=1 -f db/migrations/03_contract.sql   # только в релизе v1.10.0
```
