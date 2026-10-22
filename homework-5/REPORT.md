# Отчёт по практической работе (homework-5)

**ФИО:** Леонов Тимофей Андреевич<br>
**Группа:** И14-23-9<br>
**Вариант:** 10

## Ссылки

- Код контроллера: [`src/Campus.Sports.Api/Controllers/SportsBookingsController.cs`](src/Campus.Sports.Api/Controllers/SportsBookingsController.cs)
- SQL-скрипты:
  - [`db/migrations/01_expand.sql`](db/migrations/01_expand.sql)
  - [`db/migrations/02_migrate.sql`](db/migrations/02_migrate.sql)
  - [`db/migrations/03_contract.sql`](db/migrations/03_contract.sql)
- ADR: [`docs/adr/0009-timeslot-to-timerange.md`](docs/adr/0009-timeslot-to-timerange.md)
- Changelog: [`CHANGELOG.md`](CHANGELOG.md)

## Change Request на релиз v1.9.0

**Описание релиза.** Релиз `campus-sports-api` v1.9.0 (задача CAMPUS-110) меняет формат интервала
бронирования площадок спортивного комплекса: вместо строки `TimeSlot` (`"2026-11-05 18:00-19:30"`)
вводится структура `TimeRange`, в PostgreSQL — тип `tstzrange`. В релиз входят фазы Expand и Migrate:
добавляется новая колонка, приложение пишет данные одновременно в старую и новую колонки,
исторические брони переносятся в новый формат. API принимает и возвращает оба формата.
В запросы добавлены теги OpenTelemetry `jira.issue` и `campus.booking_id`.

**Бизнес-цель.** Исключить двойное бронирование одной площадки, хранить время брони однозначно
(в UTC) для всех клиентов — мобильного приложения, сайта и турникетов спорткомплекса — и подготовить
основу для поиска свободных окон.

**Окно обслуживания: среда, 28.10.2026, 03:00–05:00 (МСК).** Обоснование:

- по данным мониторинга это время минимального трафика (около 2 запросов в секунду против 180 в пике
  с 17:00 до 21:00);
- спорткомплекс закрыт с 23:00 до 07:00, турникеты не работают;
- ночной бэкап базы завершается в 02:30, к началу работ есть свежая резервная копия;
- до открытия спорткомплекса остаётся 2 часа запаса на откат;
- среда выбрана, потому что в понедельник открывается запись на неделю, а после пятничного релиза
  проблема осталась бы на выходные без дежурной команды.

**План проведения работ:**

1. 03:00 — зафиксировать текущую ревизию:
   `kubectl rollout history deployment/campus-sports-api -n campus-sports`.
2. 03:05 — выполнить `01_expand.sql` (фаза Expand).
3. 03:10 — выкатить новую версию:
   `kubectl set image deployment/campus-sports-api api=registry.campus.example/campus-sports-api:1.9.0 -n campus-sports`.
4. 03:12 — дождаться завершения rolling update:
   `kubectl rollout status deployment/campus-sports-api -n campus-sports --timeout=5m`.
5. 03:20 — запустить Production Sanity-тест (`dotnet test --filter "Category=Sanity"`).
6. 03:25 — выполнить `02_migrate.sql` (перенос исторических данных) и проверить отсутствие ошибок.
7. 03:35–04:30 — наблюдение за метриками (ошибки 5xx, время ответа, блокировки БД).
8. 04:30 — закрыть CR, внести запись в CHANGELOG.

**План отката.** Откат выполняется, если доля ошибок 5xx превышает 1 % в течение 5 минут, время ответа
p95 больше 800 мс, Sanity-тест не прошёл или выкат не завершился за 5 минут:

```bash
kubectl rollout undo deployment/campus-sports-api -n campus-sports
kubectl rollout status deployment/campus-sports-api -n campus-sports --timeout=5m
```

Откат базы данных не требуется: после фазы Expand схема обратно совместима, а старая колонка
`time_slot` продолжает заполняться. При проблеме в самой схеме выполняется `01_expand_rollback.sql`.
Удаление старой колонки (`03_contract.sql`) выполняется отдельным релизом v1.10.0.
