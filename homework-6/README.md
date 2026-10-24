# Практическая работа 6 — сопровождение АРМ оператора ИС «Умный кампус» (CALS, Patch Management)

**ФИО:** Леонов Тимофей Андреевич<br>
**Группа:** И14-23-9<br>
**Вариант:** «Тестер Health Check после обновления» (API: `https://api.ipify.org?format=json`)

## Ссылки

- Регламент обновления (Canary / Blue-Green, резервное копирование, план отката): [`UPDATE_GUIDE.md`](UPDATE_GUIDE.md)
- Руководство оператора: [`OPERATOR_MANUAL.md`](OPERATOR_MANUAL.md)
- Журнал изменений: [`CHANGELOG.md`](CHANGELOG.md)
- Утилита C# .NET 8: [`src/Campus.Arm.Updater/`](src/Campus.Arm.Updater/) (решение [`Campus.Arm.Updater.sln`](Campus.Arm.Updater.sln))
- Sanity-тест: [`tests/Campus.Arm.Updater.Tests/SanityTests.cs`](tests/Campus.Arm.Updater.Tests/SanityTests.cs)
- Примеры актов готовности АРМ: [`docs/examples/`](docs/examples/)
- Отчёт (PDF): [`report/ПР6_03.01_Леонов_вариант10.pdf`](report/ПР6_03.01_Леонов_вариант10.pdf)

## Запуск

```bat
dotnet build Campus.Arm.Updater.sln
cd src\Campus.Arm.Updater\bin\Debug\net8.0
Campus.Arm.Updater demo-init                 :: стенд: АРМ 2.4.0 и сервер обновлений
Campus.Arm.Updater update --version 2.5.0    :: штатное обновление
Campus.Arm.Updater update --version 2.5.1    :: сбой миграции → автоматический откат
Campus.Arm.Updater healthcheck               :: цепочка проверок и акт готовности
Campus.Arm.Updater rollback                  :: ручной откат
dotnet test Campus.Arm.Updater.sln           :: sanity-тесты
```
