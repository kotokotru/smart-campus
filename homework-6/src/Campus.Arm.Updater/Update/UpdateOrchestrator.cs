using System.Text.Json;
using Campus.Arm.Updater.Config;
using Campus.Arm.Updater.Health;
using Campus.Arm.Updater.Infrastructure;
using Campus.Arm.Updater.Reporting;

namespace Campus.Arm.Updater.Update;

/// <summary>
/// Регламент обновления АРМ (Patch Management), реализованный в коде:
/// <list type="number">
/// <item>получение манифеста с сервера обновлений и выбор релиза для кольца АРМ (Canary / Stable);</item>
/// <item>загрузка пакета и проверка SHA-256 — до любых изменений на АРМ;</item>
/// <item>резервное копирование appsettings.json, кэша и SQLite;</item>
/// <item>установка в неактивный слот (Blue-Green) и миграции БД;</item>
/// <item>переключение слота и цепочка Health Check (целостность → БД → API → считыватель);</item>
/// <item>при провале критичной проверки — автоматический откат: слот назад + восстановление данных;</item>
/// <item>формирование акта готовности.</item>
/// </list>
/// </summary>
public sealed class UpdateOrchestrator
{
    private readonly UpdaterOptions _options;
    private readonly AppLogger _log;
    private readonly ArmLayout _layout;
    private readonly Func<HttpMessageHandler>? _apiHandler;

    public UpdateOrchestrator(UpdaterOptions options, AppLogger log, Func<HttpMessageHandler>? apiHandler = null)
    {
        _options = options;
        _log = log;
        _layout = new ArmLayout(options.InstallRoot);
        _apiHandler = apiHandler;
    }

    /// <summary>Путь к последнему сформированному акту (для тестов и вывода в консоль).</summary>
    public string? LastActPath { get; private set; }

    // Команда update
    public async Task<int> UpdateAsync(string? targetVersion, CancellationToken ct = default)
    {
        var started = DateTime.Now;
        var notes = new List<string>();
        var activeSlot = _layout.ReadActiveSlot();
        var current = _layout.ReadVersion(activeSlot)
                      ?? throw new InvalidDataException($"В активном слоте '{activeSlot}' нет установленной версии");

        _log.Step($"[UPDATE] АРМ {_options.ArmId}, кольцо '{_options.Ring}', текущая версия {current.Version} (слот '{activeSlot}')");

        // 1. Манифест сервера обновлений
        ReleaseInfo? release;
        try
        {
            release = SelectRelease(LoadManifest(), current, targetVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _log.Error($"[UPDATE] Сервер обновлений недоступен или манифест повреждён: {ex.Message}");
            return ExitCodes.Aborted;
        }

        if (release is null) return ExitCodes.Ok;
        _log.Step($"[UPDATE] Выбран релиз {release.Version} ({string.Join("/", release.Rings)}): {release.Notes}");
        notes.Add($"Релиз {release.Version} получен с сервера обновлений ({release.Notes})");

        // 2. Загрузка пакета и контроль SHA-256
        string package;
        try
        {
            package = Download(release);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.Error($"[UPDATE] Обновление прервано до установки: {ex.Message}");
            _log.Ok($"[UPDATE] Изменения на АРМ не вносились, работает версия {current.Version}");
            notes.Add($"Пакет отклонён: {ex.Message}. Установка не выполнялась.");
            await HealthAndActAsync($"Обновление {current.Version} → {release.Version} (прервано)", current.Version,
                current.Version, null, rowsBefore: null, rolledBack: false, notes, started, ct, forceNotReady: true);
            return ExitCodes.Aborted;
        }

        // 3. Снимок данных и резервная копия
        Dictionary<string, long> rowsBefore;
        using (var db = LocalDb.Open(_layout.Database, readOnly: true))
            rowsBefore = LocalDb.CountRows(db, LocalDb.KeyTables);
        _log.Info($"[UPDATE] Снимок данных до обновления: {string.Join(", ", rowsBefore.Select(kv => $"{kv.Key}={kv.Value}"))}");

        var backups = new BackupService(_layout, _options.BackupRoot, _log, _options.BackupsToKeep);
        BackupInfo backup;
        try
        {
            backup = backups.Create(current.Version, activeSlot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            _log.Error($"[UPDATE] Не удалось создать резервную копию: {ex.Message}. Обновление без копии запрещено регламентом.");
            return ExitCodes.Aborted;
        }
        notes.Add($"Резервная копия данных: {Paths.Rel(backup.Path)}");

        // 4. Установка в неактивный слот + миграции
        var targetSlot = ArmLayout.OtherSlot(activeSlot);
        var installer = new BlueGreenInstaller(_layout, _log);
        AppVersionInfo installed;
        try
        {
            installed = installer.InstallToSlot(package, targetSlot);
            var schema = installer.ApplyMigrations(targetSlot);
            notes.Add($"Версия {installed.Version} установлена в слот '{targetSlot}', схема БД v{schema}");
        }
        catch (Exception ex)
        {
            // Активный слот не переключался — достаточно вернуть данные (миграция могла частично пройти).
            _log.Error($"[UPDATE] Ошибка установки: {ex.Message}");
            backups.Restore(backup);
            DiscardFailedSlot(targetSlot, release.Version);
            notes.Add($"Ошибка установки: {ex.Message}. Данные восстановлены, слот не переключался.");
            await HealthAndActAsync($"Обновление {current.Version} → {release.Version}", current.Version, release.Version,
                backup.Path, rowsBefore, rolledBack: true, notes, started, ct);
            return ExitCodes.RolledBack;
        }

        // 5. Переключение Blue-Green
        _layout.SwitchActiveSlot(targetSlot);
        _log.Step($"[SWITCH] Активный слот: '{activeSlot}' → '{targetSlot}' (версия {installed.Version})");
        notes.Add($"Переключение слота {activeSlot} → {targetSlot}");

        // 6. Health Check новой версии
        var results = await RunChecksAsync(rowsBefore, ct);
        var failed = results.Where(r => r.Critical && r.Status == HealthStatus.Fail).ToList();

        if (failed.Count == 0)
        {
            _log.Ok($"[UPDATE] Обновление {current.Version} → {installed.Version} выполнено успешно");
            notes.Add("Все критичные проверки пройдены");
            Finish(new ReadinessReport
            {
                ArmId = _options.ArmId, Ring = _options.Ring,
                Operation = $"Обновление {current.Version} → {installed.Version}",
                VersionBefore = current.Version, VersionAfter = installed.Version,
                ActiveSlot = targetSlot, BackupPath = backup.Path,
                StartedAt = started, Results = results, Notes = notes
            });
            return ExitCodes.Ok;
        }

        // 7. Автоматический откат
        _log.Error($"[ROLLBACK] Провалены критичные проверки: {string.Join(", ", failed.Select(f => f.Code))}. Запуск отката.");
        notes.Add($"Провалены проверки: {string.Join("; ", failed.Select(f => $"{f.Name} — {f.Details}"))}");

        _layout.SwitchActiveSlot(activeSlot);
        _log.Warn($"[ROLLBACK] Активный слот возвращён: '{targetSlot}' → '{activeSlot}' (версия {current.Version})");
        backups.Restore(backup);
        notes.Add($"Откат: слот {targetSlot} → {activeSlot}, данные восстановлены из резервной копии");
        DiscardFailedSlot(targetSlot, installed.Version);

        _log.Step("[ROLLBACK] Контрольная проверка предыдущей версии после отката");
        var after = await RunChecksAsync(rowsBefore, ct);
        var restoredOk = after.All(r => !(r.Critical && r.Status == HealthStatus.Fail));
        notes.Add(restoredOk
            ? $"После отката версия {current.Version} прошла все критичные проверки"
            : "ВНИМАНИЕ: после отката есть непройденные проверки — требуется вмешательство администратора");

        Finish(new ReadinessReport
        {
            ArmId = _options.ArmId, Ring = _options.Ring,
            Operation = $"Обновление {current.Version} → {installed.Version} (откат)",
            VersionBefore = current.Version, VersionAfter = installed.Version,
            ActiveSlot = activeSlot, BackupPath = backup.Path,
            StartedAt = started, Results = results.Concat(after.Select(r => r with { Name = r.Name + " (после отката)" })).ToList(),
            Notes = notes, RolledBack = true
        });
        return ExitCodes.RolledBack;
    }

    // Команда healthcheck
    public async Task<int> HealthCheckAsync(CancellationToken ct = default)
    {
        var started = DateTime.Now;
        var slot = _layout.ReadActiveSlot();
        var version = _layout.ReadVersion(slot)?.Version ?? "не установлена";
        var report = await HealthAndActAsync("Плановая проверка готовности", version, version, null, null,
            rolledBack: false, [], started, ct);
        return report.Ready ? ExitCodes.Ok : ExitCodes.NotReady;
    }

    // Команда rollback (ручной откат администратором)
    public async Task<int> RollbackAsync(CancellationToken ct = default)
    {
        var started = DateTime.Now;
        var activeSlot = _layout.ReadActiveSlot();
        var targetSlot = ArmLayout.OtherSlot(activeSlot);
        var current = _layout.ReadVersion(activeSlot)
                      ?? throw new InvalidDataException($"В активном слоте '{activeSlot}' нет установленной версии");

        // 1. Точка восстановления: последняя резервная копия, сделанная на версии ниже текущей
        var backup = FindRollbackPoint(current.Version);
        if (backup is null)
        {
            _log.Error($"[ROLLBACK] Нет резервной копии версии ниже {current.Version} — откат невозможен");
            return ExitCodes.Aborted;
        }
        _log.Step($"[ROLLBACK] Ручной откат {current.Version} → {backup.Version}, точка восстановления {Paths.Rel(backup.Path)}");
        var notes = new List<string> { $"Ручной откат {current.Version} → {backup.Version}" };

        // 2. Предыдущая версия должна лежать в резервном слоте; если её там нет — переустановить с сервера
        if (_layout.ReadVersion(targetSlot)?.Version != backup.Version)
        {
            try
            {
                var release = LoadManifest().Releases.FirstOrDefault(r => r.Version == backup.Version)
                              ?? throw new InvalidDataException($"пакет версии {backup.Version} отсутствует на сервере обновлений");
                new BlueGreenInstaller(_layout, _log).InstallToSlot(Download(release), targetSlot);
                notes.Add($"Версия {backup.Version} переустановлена в слот '{targetSlot}' с сервера обновлений");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                _log.Error($"[ROLLBACK] Не удалось подготовить версию {backup.Version}: {ex.Message}. Работает версия {current.Version}.");
                return ExitCodes.Aborted;
            }
        }

        // 3. Переключение слота и восстановление данных
        _layout.SwitchActiveSlot(targetSlot);
        _log.Warn($"[ROLLBACK] Активный слот: '{activeSlot}' ({current.Version}) → '{targetSlot}' ({backup.Version})");
        new BackupService(_layout, _options.BackupRoot, _log, _options.BackupsToKeep).Restore(backup);
        notes.Add($"Данные восстановлены из {Paths.Rel(backup.Path)}");

        // 4. Контрольная проверка и акт
        var report = await HealthAndActAsync($"Ручной откат {current.Version} → {backup.Version}", current.Version,
            backup.Version, backup.Path, null, rolledBack: false, notes, started, ct);
        return report.Ready ? ExitCodes.Ok : ExitCodes.NotReady;
    }

    // Команды status / check
    public int Status()
    {
        var slot = _layout.ReadActiveSlot();
        var v = _layout.ReadVersion(slot);
        var other = _layout.ReadVersion(ArmLayout.OtherSlot(slot));
        _log.Step($"[STATUS] АРМ {_options.ArmId}, кольцо '{_options.Ring}'");
        _log.Info($"[STATUS] Активный слот '{slot}': {v?.Version ?? "пусто"}; резервный слот '{ArmLayout.OtherSlot(slot)}': {other?.Version ?? "пусто"}");
        using var db = LocalDb.Open(_layout.Database, readOnly: true);
        _log.Info($"[STATUS] Схема БД v{LocalDb.GetSchemaVersion(db)}; " +
                  string.Join(", ", LocalDb.CountRows(db, LocalDb.KeyTables).Select(kv => $"{kv.Key}={kv.Value}")));
        return ExitCodes.Ok;
    }

    public int Check()
    {
        var current = _layout.ReadVersion(_layout.ReadActiveSlot());
        ReleaseManifest manifest;
        try { manifest = LoadManifest(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Error($"[CHECK] Сервер обновлений недоступен: {ex.Message}");
            return ExitCodes.Aborted;
        }

        _log.Step($"[CHECK] Релизы на сервере (текущая версия {current?.Version}, кольцо '{_options.Ring}'):");
        foreach (var r in manifest.Releases.OrderBy(r => r.ParsedVersion))
        {
            var available = r.Rings.Contains(_options.Ring, StringComparer.OrdinalIgnoreCase);
            _log.Info($"  {r.Version,-8} кольца [{string.Join(",", r.Rings)}] {(available ? "доступен" : "НЕ доступен для этого кольца")} — {r.Notes}");
        }
        return ExitCodes.Ok;
    }

    // Вспомогательные методы
    private ReleaseManifest LoadManifest()
    {
        var file = Path.Combine(_options.UpdateSource, "manifest.json");
        var text = Retry.Io(() => File.ReadAllText(file), _log, "Чтение манифеста");
        return JsonSerializer.Deserialize<ReleaseManifest>(text, Json.Options)
               ?? throw new InvalidDataException("Пустой манифест сервера обновлений");
    }

    /// <summary>Выбор релиза с учётом кольца развёртывания (Canary Deployment).</summary>
    private ReleaseInfo? SelectRelease(ReleaseManifest manifest, AppVersionInfo current, string? targetVersion)
    {
        var currentVersion = Version.Parse(current.Version);
        var forRing = manifest.Releases
            .Where(r => r.Rings.Contains(_options.Ring, StringComparer.OrdinalIgnoreCase))
            .ToList();

        ReleaseInfo? release;
        if (targetVersion is not null)
        {
            release = manifest.Releases.FirstOrDefault(r => r.Version == targetVersion);
            if (release is null)
            {
                _log.Warn($"[UPDATE] Релиз {targetVersion} на сервере не найден");
                return null;
            }
            if (!forRing.Contains(release))
            {
                _log.Warn($"[UPDATE] Релиз {targetVersion} ещё не доступен кольцу '{_options.Ring}' (только [{string.Join(",", release.Rings)}])");
                return null;
            }
        }
        else
        {
            release = forRing.Where(r => r.ParsedVersion > currentVersion).MaxBy(r => r.ParsedVersion);
        }

        if (release is null || release.ParsedVersion <= currentVersion)
        {
            _log.Ok($"[UPDATE] Обновлений нет: установлена актуальная версия {current.Version}");
            return null;
        }
        return release;
    }

    /// <summary>«Загрузка» пакета с сервера (копирование из сетевой папки) и проверка SHA-256.</summary>
    private string Download(ReleaseInfo release)
    {
        var source = Path.Combine(_options.UpdateSource, release.Package);
        Directory.CreateDirectory(_layout.DownloadsDir);
        var target = Path.Combine(_layout.DownloadsDir, Path.GetFileName(release.Package));

        Retry.Io(() => { File.Copy(source, target, overwrite: true); return 0; }, _log, "Загрузка пакета");
        var size = new FileInfo(target).Length;
        var hash = Hashing.Sha256File(target);
        _log.Info($"[DOWNLOAD] {Path.GetFileName(target)}: {size / 1024.0:0.0} КБ, SHA-256 {hash[..16]}…");

        if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new InvalidDataException($"контрольная сумма пакета {release.Version} не совпадает с манифестом " +
                                           $"(ожидалось {release.Sha256[..16]}…, получено {hash[..16]}…) — пакет повреждён или подменён");
        }
        _log.Ok("[DOWNLOAD] Контрольная сумма пакета совпадает с манифестом");
        return target;
    }

    /// <summary>Последняя проверенная резервная копия, сделанная на версии ниже текущей.</summary>
    private BackupInfo? FindRollbackPoint(string currentVersion)
    {
        if (!Directory.Exists(_options.BackupRoot)) return null;
        var current = Version.Parse(currentVersion);
        return new DirectoryInfo(_options.BackupRoot).GetDirectories()
            .OrderByDescending(d => d.Name, StringComparer.Ordinal)
            .Select(d => Path.Combine(d.FullName, "backup.json"))
            .Where(File.Exists)
            .Select(f => JsonSerializer.Deserialize<BackupInfo>(File.ReadAllText(f), Json.Options))
            .FirstOrDefault(b => b is not null && Version.TryParse(b.Version, out var v) && v < current);
    }

    /// <summary>Слот с версией, не прошедшей проверку, очищается — её нельзя случайно «вернуть» ручным откатом.</summary>
    private void DiscardFailedSlot(string slot, string version)
    {
        try
        {
            Directory.Delete(_layout.SlotDir(slot), recursive: true);
            _log.Info($"[ROLLBACK] Слот '{slot}' с непроверенной версией {version} очищен");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"[ROLLBACK] Не удалось очистить слот '{slot}': {ex.Message}");
        }
    }

    private IReadOnlyList<IHealthCheck> BuildChecks() =>
    [
        new IntegrityCheck(),
        new DatabaseCheck(),
        new ApiCheck(_apiHandler),
        new ReaderCheck(ReaderCheck.Create(_options.HealthCheck.Reader, _layout.ReaderTestCard))
    ];

    private Task<IReadOnlyList<HealthCheckResult>> RunChecksAsync(IReadOnlyDictionary<string, long>? rowsBefore, CancellationToken ct) =>
        new HealthCheckRunner(BuildChecks()).RunAsync(new HealthContext
        {
            Layout = _layout,
            Options = _options,
            Log = _log,
            RowsBefore = rowsBefore
        }, ct);

    private async Task<ReadinessReport> HealthAndActAsync(string operation, string before, string after, string? backupPath,
        IReadOnlyDictionary<string, long>? rowsBefore, bool rolledBack, List<string> notes, DateTime started,
        CancellationToken ct, bool forceNotReady = false)
    {
        var results = await RunChecksAsync(rowsBefore, ct);
        if (forceNotReady)
            results = results.Append(new HealthCheckResult("PACKAGE", "Пакет обновления", HealthStatus.Fail, 0,
                "пакет отклонён, обновление не установлено", true)).ToList();

        var report = new ReadinessReport
        {
            ArmId = _options.ArmId, Ring = _options.Ring, Operation = operation,
            VersionBefore = before, VersionAfter = after,
            ActiveSlot = _layout.ReadActiveSlot(), BackupPath = backupPath,
            StartedAt = started, Results = results, Notes = notes, RolledBack = rolledBack
        };
        Finish(report);
        return report;
    }

    private void Finish(ReadinessReport report)
    {
        ReadinessAct.PrintSummary(report);
        try
        {
            LastActPath = ReadinessAct.Write(report, _options.ReportsRoot);
            _log.Ok($"[ACT] Акт готовности сохранён: {Paths.Rel(LastActPath)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"[ACT] Не удалось сохранить акт: {ex.Message}");
        }
        if (_log.FilePath is not null) _log.Info($"[LOG] Журнал: {Paths.Rel(_log.FilePath)}");
    }
}
