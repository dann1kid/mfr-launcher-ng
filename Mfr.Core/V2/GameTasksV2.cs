using Mfr.Core.Exceptions;
using Mfr.Core.Storage;
using Mfr.Core.Tasks;
using Mfr.Core.Services;
using Mfr.Core.Network;
using Mfr.Protocol.Dto;
using ProtoSchema = Com.Lezenford.Mfr.Schema.V1.Schema;
using SchemaFile = Com.Lezenford.Mfr.Schema.V1.File;
using VersionFile = Com.Lezenford.Mfr.Version.V1.File;
using ProtoVersion = Com.Lezenford.Mfr.Version.V1.Version;

namespace Mfr.Core.V2;

/// <summary>Common manifest plumbing of the v2 game tasks.</summary>
/// <summary>К-2: линии нет на сервере — состояние постоянное. Отдельна от «пока нечего отдать».</summary>
public sealed class LineUnavailableException(string line)
    : Exception($"Линия {line} больше не выпускается сервером");

/// <summary>К-3: в линии временно нет выпуска — состояние временное, повтор придёт по опросу.</summary>
public sealed class NothingToServeException(string line)
    : Exception($"Для линии {line} пока нет выпуска");

public static class V2Manifests
{
    public const string SchemaFileName = "schema.proto";

    public static async Task<(ProtoSchema Schema, ProtoVersion Plan, string Host)> LoadAsync(
        LauncherServicesV2 services, Region region, string line, CancellationToken ct)
    {
        var version = await services.Api.GetGameVersion(line, region, ct).ConfigureAwait(false);
        if (version.State == LineVersionState.NoSuchLine)
        {
            throw new LineUnavailableException(line);
        }
        if (version.State != LineVersionState.Found || version.Id is null)
        {
            throw new NothingToServeException(line);
        }
        var details = await services.Api.GetGameFiles(version.Id, region, ct).ConfigureAwait(false);
        var schemaBytes = await services.Downloader.DownloadManifestAsync(details.Host, details.Schema, details.CompressedSchema, ct).ConfigureAwait(false);
        var planBytes = await services.Downloader.DownloadManifestAsync(details.Host, details.Files, details.CompressedFiles, ct).ConfigureAwait(false);
        var schema = ProtoSchema.Parser.ParseFrom(schemaBytes);
        var plan = ProtoVersion.Parser.ParseFrom(planBytes);
        return (schema, plan, details.Host);
    }

    /// <summary>Manifests of a concrete version (pack downloads skip the line lookup).</summary>
    public static async Task<(ProtoSchema Schema, ProtoVersion Plan, string Host)> LoadForVersionAsync(
        LauncherServicesV2 services, Region region, string version, CancellationToken ct)
    {
        var details = await services.Api.GetGameFiles(version, region, ct).ConfigureAwait(false);
        var schemaBytes = await services.Downloader.DownloadManifestAsync(details.Host, details.Schema, details.CompressedSchema, ct).ConfigureAwait(false);
        var planBytes = await services.Downloader.DownloadManifestAsync(details.Host, details.Files, details.CompressedFiles, ct).ConfigureAwait(false);
        return (ProtoSchema.Parser.ParseFrom(schemaBytes), ProtoVersion.Parser.ParseFrom(planBytes), details.Host);
    }

    /// <summary>Контракт: файл с непустым списком ОС нужен только перечисленным ОС.</summary>
    public static bool SuitsCurrentOs(SchemaFile file) => SuitsOs(file, ApiClientV2.Os);

    /// <summary>Та же проверка для конкретного значения os — словарь контракта.</summary>
    public static bool SuitsOs(SchemaFile file, string os) =>
        file.Os.Count == 0 || file.Os.Contains(os);

    /// <summary>Every file the launcher is responsible for: required partitions and required option contents.</summary>
    public static IEnumerable<SchemaFile> MandatoryFiles(ProtoSchema schema) =>
        schema.Partitions.Where(p => p.Required).SelectMany(p => p.Files)
            .Concat(schema.Options.SelectMany(o => o.Contents)
                .Where(c => c.Partition.Required)
                .SelectMany(c => c.Partition.Files))
            .Where(SuitsCurrentOs);

    /// <summary>
    /// Files of downloaded option groups plus everything mandatory. БТ-4.2: пакет —
    /// группа манифеста, поэтому граница проходит по группе: одинаковое имя варианта
    /// в чужой (нескачанной) группе пакетом не становится и гигабайты чужих пакетов не тянет.
    /// </summary>
    public static IEnumerable<SchemaFile> ActiveFiles(ProtoSchema schema, IReadOnlyCollection<string> downloadedGroups)
    {
        foreach (var file in MandatoryFiles(schema))
        {
            yield return file;
        }
        foreach (var file in schema.Options
            .Where(group => downloadedGroups.Contains(group.Name))
            .SelectMany(group => group.Contents)
            .Where(c => !c.Partition.Required)
            .SelectMany(c => c.Partition.Files)
            .Where(SuitsCurrentOs))
        {
            yield return file;
        }
    }

    public static void SaveSchemaFile(LauncherServices core, ProtoSchema schema)
    {
        Directory.CreateDirectory(core.Paths.Root);
        System.IO.File.WriteAllBytes(Path.Combine(core.Paths.Root, SchemaFileName), Google.Protobuf.MessageExtensions.ToByteArray(schema));
    }

    public static ProtoSchema? TryLoadLocalSchema(LauncherServices core)
    {
        var path = Path.Combine(core.Paths.Root, SchemaFileName);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return ProtoSchema.Parser.ParseFrom(File.ReadAllBytes(path));
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}

/// <summary>
/// v2 install: picks a compatibility line,
/// fetches the protobuf manifests, downloads mandatory files with optional
/// copies, refreshes the option schema, applies default presets.
/// </summary>
public sealed class GameInstallV2Task(LauncherServicesV2 services) : LauncherTask<object?, object?>(services.Core)
{
    protected override async Task<object?> Action(object? parameters, CancellationToken ct)
    {
        Describe("Подготовка к установке");
        var core = Services;
        var repository = core.Repository;

        var region = services.Region;
        var line = repository.GetProperty(PropertyKeys.SelectedBuild)
            ?? (await services.Api.GetChannels(region, ct).ConfigureAwait(false)).FirstOrDefault()
            ?? throw new ServerMaintenanceException();

        var (schema, plan, host) = await V2Manifests.LoadAsync(services, region, line, ct).ConfigureAwait(false);

        // БТ-1.4: одноразовая очистка перехода — только по состоянию, импортированному
        // из установленного клиента, и только после получения манифеста новой версии
        if (repository.GetProperty(PropertyKeys.ImportedLegacyState) is not null &&
            repository.GetProperty(PropertyKeys.OldReleaseRemoved) is null)
        {
            Describe("Очистка после перехода");
            foreach (var entry in Directory.EnumerateFileSystemEntries(core.Paths.Root))
            {
                var name = Path.GetFileName(entry);
                if (name is "Saves" or "screenshots")
                {
                    continue;
                }
                TryDeleteRecursive(entry);
            }
            repository.SetProperty(PropertyKeys.OldReleaseRemoved, "true");
        }

        V2Manifests.SaveSchemaFile(core, schema);
        repository.SetProperty(PropertyKeys.SelectedBuild, line);

        var planByPath = plan.Files.ToDictionary(f => f.Path);
        var mandatory = V2Manifests.MandatoryFiles(schema)
            .Where(f => f.MainPath is not null)
            .ToList();

        // БТ-1: проверка свободного места, когда схема несёт размеры всех файлов
        if (mandatory.All(f => f.HasSize))
        {
            var needKb = (long)(mandatory.Sum(f => (double)f.Size) * 1.1 / 1024);
            // Linux: DriveInfo(path) делает statvfs для конкретного пути (много ФС);
            // Windows: нужен корень диска
            var driveRoot = OperatingSystem.IsWindows()
                ? Path.GetPathRoot(core.Paths.Root)!
                : core.Paths.Root;
            var drive = new DriveInfo(driveRoot);
            if (drive.AvailableFreeSpace / 1024 < needKb)
            {
                throw new InvalidOperationException(
                    $"Для установки нужно минимум {needKb} КБ свободного места. Доступно {drive.AvailableFreeSpace / 1024} КБ");
            }
        }
        if (mandatory.Any(f => !planByPath.ContainsKey(f.MainPath)))
        {
            throw new InvalidOperationException("Inconsistent files");
        }

        var files = mandatory.Select(f => new V2FilePlan(f.MainPath, f.HasOptionalPath ? f.OptionalPath : null, planByPath[f.MainPath].Storage, planByPath[f.MainPath].HasCompressedStorage ? planByPath[f.MainPath].CompressedStorage : null, f.Sha256.ToByteArray(), f.Compression, f.HasSize ? f.Size : null)).ToList();

        await JoinAsync(new DownloadFilesV2Task(services), new V2DownloadParameters(host, files, ApplyOptionalPath: true), ct).ConfigureAwait(false);

        Report(100);
        Describe("Анализ схемы");
        var optionFiles = repository.GetSections()
            .SelectMany(s => s.Options)
            .Where(o => o.Applied)
            .SelectMany(o => o.Files)
            .Select(f => f.GamePath)
            .ToHashSet();
        if (optionFiles.Count > 0)
        {
            await JoinAsync(new CheckConsistencyV2Task(services), null, ct).ConfigureAwait(false);
        }
        else
        {
            await JoinAsync(new FillSchemaV2Task(services), null, ct).ConfigureAwait(false);
        }

        repository.SetProperty(PropertyKeys.NewReleaseInstalled, "true");
        repository.SetProperty(PropertyKeys.GameInstalled, "true");

        Services.Mge.ApplyConfig(MgeConfiguration.MIDDLE, backup: false);
        Services.OpenMw.PrepareTemplates();
        Services.OpenMw.ApplyConfig(OpenMwConfiguration.MIDDLE, backup: false);

        System.Diagnostics.Process? mge = null;
        try
        {
            mge = Services.Runner.StartMge();
        }
        catch (Exception)
        {
            // БТ-1.3: без настройщика установка всё равно успешна
        }
        if (mge is null)
        {
            return null;
        }
        using (mge)
        await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        if (!mge.HasExited)
        {
            mge.Kill();
        }
        return null;
    }

    private static void TryDeleteRecursive(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                System.IO.File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// v2 update: diffs the installed schema.proto
/// against the fresh one by SHA-256, downloads changed files, removes gone
/// ones, then refreshes the option schema and re-applies the applied options.
/// </summary>
public sealed class GameUpdateV2Task(LauncherServicesV2 services) : LauncherTask<object?, object?>(services.Core)
{
    protected override async Task<object?> Action(object? parameters, CancellationToken ct)
    {
        Describe("Подготовка к обновлению");
        var core = Services;
        var repository = core.Repository;

        var installedLine = repository.GetProperty(PropertyKeys.SelectedBuild)
            ?? VersionLine.Of(V2Manifests.TryLoadLocalSchema(core)?.Version)
            ?? throw new InvalidOperationException("Не удалось определить установленную линию");

        var (schema, plan, host) = await V2Manifests.LoadAsync(services, services.Region, installedLine, ct).ConfigureAwait(false);
        var current = V2Manifests.TryLoadLocalSchema(core)
            ?? throw new InvalidOperationException("schema.proto отсутствует — переустановите сборку");

        var downloadedGroups = repository.GetSections()
            .Where(s => s.Downloaded)
            .Select(s => s.Name)
            .ToHashSet();

        var currentFiles = V2Manifests.ActiveFiles(current, downloadedGroups)
            .Where(f => f.MainPath is not null)
            .GroupBy(f => f.MainPath)
            .ToDictionary(g => g.Key, g => g.First());
        var newFiles = V2Manifests.ActiveFiles(schema, downloadedGroups)
            .Where(f => f.MainPath is not null)
            .GroupBy(f => f.MainPath)
            .ToDictionary(g => g.Key, g => g.First());

        var forRemove = currentFiles.Where(kvp => !newFiles.ContainsKey(kvp.Key)).Select(kvp => kvp.Value).ToList();
        var forDownload = newFiles
            .Where(kvp => !currentFiles.TryGetValue(kvp.Key, out var old) || !old.Sha256.ToByteArray().AsSpan().SequenceEqual(kvp.Value.Sha256.ToByteArray()))
            .Select(kvp => kvp.Value)
            .ToList();
        if (forDownload.Any(f => !plan.Files.Any(p => p.Path == f.MainPath)))
        {
            throw new InvalidOperationException("Inconsistent files");
        }

        if (forDownload.Count > 0)
        {
            var files = forDownload.Select(f =>
            {
                var entry = plan.Files.First(p => p.Path == f.MainPath);
                return new V2FilePlan(
                    f.MainPath,
                    f.HasOptionalPath ? f.OptionalPath : null,
                    entry.Storage,
                    entry.HasCompressedStorage ? entry.CompressedStorage : null,
                    f.Sha256.ToByteArray(),
                    f.Compression,
                    f.HasSize ? f.Size : null);
            }).ToList();
            await JoinAsync(new DownloadFilesV2Task(services), new V2DownloadParameters(host, files, ApplyOptionalPath: false), ct).ConfigureAwait(false);
        }

        foreach (var removed in forRemove)
        {
            TryDelete(core.Paths.Resolve(removed.MainPath));
            if (removed.HasOptionalPath)
            {
                TryDelete(core.Paths.Resolve(removed.OptionalPath));
            }
        }

        V2Manifests.SaveSchemaFile(core, schema);
        Report(0);
        Describe("Проверка состояния");

        await JoinAsync(new FillSchemaV2Task(services), null, ct).ConfigureAwait(false);

        // БТ-4.4: заново применяются только варианты, чьих файлов коснулось обновление
        var affected = forDownload.Select(f => f.MainPath)
            .Concat(forRemove.Where(f => f.HasOptionalPath).Select(f => f.OptionalPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filesForApply = new List<(Option? Current, Option Target)>();
        foreach (var section in repository.GetSections().Where(s => s.Downloaded))
        {
            var applied = section.Options.FirstOrDefault(o => o.Applied);
            if (applied != null && applied.Files.Any(file => affected.Contains(file.GamePath)))
            {
                filesForApply.Add((null, applied));
            }
        }
        if (filesForApply.Count > 0)
        {
            await JoinAsync(new ApplyOptionsTask(services.Core), filesForApply, ct).ConfigureAwait(false);
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Rebuilds the option database from the local schema.proto when its bytes
/// change, preserving downloaded/applied flags and auto-detecting the applied
/// option by SHA-256.
/// </summary>
public sealed class FillSchemaV2Task(LauncherServicesV2 services) : LauncherTask<object?, object?>(services.Core)
{
    protected override async Task<object?> Action(object? parameters, CancellationToken ct)
    {
        Describe("Обновление настраиваемых компонентов");
        var schema = V2Manifests.TryLoadLocalSchema(Services)
            ?? throw new ArgumentException("Файл схемы не найден: schema.proto");

        var schemaHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(Path.Combine(Services.Paths.Root, V2Manifests.SchemaFileName))));
        if (Services.Repository.GetProperty(PropertyKeys.Schema) == schemaHash)
        {
            return null;
        }

        var saved = Services.Repository.GetSections();
        var sections = SchemaBridge.ToSections(schema);
        foreach (var section in sections)
        {
            var savedSection = saved.FirstOrDefault(s => s.Name == section.Name);
            if (savedSection is null)
            {
                continue;
            }
            section.GetType(); // records are immutable: rebuild with saved flags below
        }
        // rebuild with flags preserved
        var rebuilt = new List<Section>();
        foreach (var section in sections)
        {
            var savedSection = saved.FirstOrDefault(s => s.Name == section.Name);
            var rebuiltSection = new Section(0, section.Name, savedSection?.Downloaded ?? false);
            foreach (var option in section.Options)
            {
                var savedOption = savedSection?.Options.FirstOrDefault(o => o.Name == option.Name);
                var rebuiltOption = new Option(0, 0, section.Name, option.Name,
                    savedOption?.Applied ?? false, option.Description, option.ImagePath);
                foreach (var file in option.Files)
                {
                    rebuiltOption.Files.Add(new OptionFile(0, 0, file.StoragePath, file.GamePath, file.Md5));
                }
                rebuiltSection.Options.Add(rebuiltOption);
            }
            rebuilt.Add(rebuiltSection);
        }

        var notApplied = rebuilt.Where(s => s.Options.All(o => !o.Applied)).ToList();
        var defaultsForApply = new List<(Section Section, Option Default)>();
        if (notApplied.Count > 0)
        {
            Describe("Поиск активной конфигурации");
            var total = notApplied.Sum(s => s.Options.Sum(o => o.Files.Count));
            var current = 0L;
            foreach (var section in notApplied)
            {
                var activeIndex = -1;
                for (var index = 0; index < section.Options.Count; index++)
                {
                    var option = section.Options[index];
                    var matches = true;
                    foreach (var file in option.Files)
                    {
                        ct.ThrowIfCancellationRequested();
                        current++;
                        Report(current, total);
                        var gamePath = Services.Paths.Resolve(file.GamePath);
                        if (!File.Exists(gamePath) || !Sha256.Matches(gamePath, file.Md5))
                        {
                            matches = false;
                            break;
                        }
                    }
                    if (matches)
                    {
                        activeIndex = index;
                        break;
                    }
                }

                if (activeIndex >= 0)
                {
                    section.Options[activeIndex] = section.Options[activeIndex] with { Applied = true };
                    continue;
                }

                // nothing recognized: the schema's required partition of this group is the default
                var defaultName = schema.Options
                    .FirstOrDefault(group => group.Name == section.Name)?.Contents
                    .FirstOrDefault(content => content.Partition?.Required == true)?.Name;
                if (defaultName is null)
                {
                    continue;
                }
                var defaultIndex = section.Options.FindIndex(o => o.Name == defaultName);
                if (defaultIndex >= 0)
                {
                    section.Options[defaultIndex] = section.Options[defaultIndex] with { Applied = true };
                    defaultsForApply.Add((section, section.Options[defaultIndex]));
                }
            }
        }

        Report(100);
        Describe("Сохранение настроек");
        Services.Repository.ReplaceSchema(rebuilt);
        Services.Repository.SetProperty(PropertyKeys.Schema, schemaHash);

        if (defaultsForApply.Count > 0)
        {
            // apply the schema defaults locally (storage copies are already in place)
            Describe("Применение опций по умолчанию");
            await JoinAsync(new Mfr.Core.Tasks.ApplyOptionsTask(Services),
                defaultsForApply.Select(d => ((Mfr.Core.Storage.Option?)null, d.Default)).ToList(), ct).ConfigureAwait(false);
            Report(100);
        }
        return null;
    }
}

/// <summary>
/// v2 consistency check: verifies active files by SHA-256, re-downloads the
/// broken ones, refreshes the schema and re-applies applied options
///.
/// </summary>
public sealed class CheckConsistencyV2Task(LauncherServicesV2 services) : LauncherTask<object?, object?>(services.Core)
{
    /// <summary>Вопрос игроку: текст и согласие. БТ-3.2 — окно показывает, что будет удалено.</summary>
    public Func<string, CancellationToken, Task<bool>>? AskUser { get; init; }

    private static readonly string[] SettingsFiles =
    [
        "Morrowind.exe",
        "Morrowind.ini",
        @"mge3\MGE.ini",
        @"Data Files\MWSE\config",
        @"mcpatch\installed",
    ];

    /// <summary>
    /// БТ-3.5: явный перечень исключений — сохранения, скриншоты, журналы и файлы,
    /// которые создают игра, движки, их утилиты и сам клиент. Относительно корня папки
    /// игры, «/»-нормализация; каталог исключается с содержимым.
    /// </summary>
    private static readonly string[] ExcludedPaths =
    [
        "Saves",
        "Screenshots",
        "screenshots",
        "Data Files/Saves",
        "Data Files/ScreenShots",
        "Data Files/MWSE/logs",
        "Data Files/MWSE/mods",
        "MGE.log",
        "Data Files/MGE.log",
        "launcher.db",
        "launcher.ini",
        "launcher.json",
        "launcher.mv.db",
        "launcher.exe",
        "launcher.exe.old",
        "schema.proto",
        "crash.log",
        "task-trace.log",
    ];

    protected override async Task<object?> Action(object? parameters, CancellationToken ct)
    {
        Describe("Проверка целостности игры");
        var core = Services;
        var repository = core.Repository;

        var schema = V2Manifests.TryLoadLocalSchema(core)
            ?? throw new ArgumentException("Файл схемы не найден: schema.proto");
        var downloadedGroups = repository.GetSections()
            .Where(s => s.Downloaded)
            .Select(s => s.Name)
            .ToHashSet();
        var active = V2Manifests.ActiveFiles(schema, downloadedGroups)
            .Where(f => f.MainPath is not null)
            .GroupBy(f => f.MainPath)
            .Select(g => g.First())
            .ToList();

        var total = active.Count;
        var current = 0;
        var modified = new List<SchemaFile>();
        var missing = new List<SchemaFile>();
        // БТ-3: сверка проходит по всей сборке (десятки гигабайт), цикл синхронный —
        // без Task.Run он занимает поток вызвавшего (UI) вплоть до первого await,
        // и окно перестаёт отвечать на всё время хеширования
        await Task.Run(() =>
        {
            foreach (var file in active)
            {
                ct.ThrowIfCancellationRequested();
                Report(++current, total);
                var mainPath = core.Paths.Resolve(file.MainPath);
                if (File.Exists(mainPath))
                {
                    if (!Sha256.Matches(mainPath, file.Sha256.ToByteArray()))
                    {
                        modified.Add(file);
                    }
                }
                else
                {
                    missing.Add(file);
                }
            }
        }, ct).ConfigureAwait(false);

        // БТ-3.4 / К-4: изменённые файлы с настройками — только с согласия;
        // отсутствующие файлы сборки качаются всегда, это не удаление настроек
        var settings = modified.Where(f => IsSettingsFile(f.MainPath)).ToList();
        if (settings.Count > 0 && AskUser != null)
        {
            var reset = await AskUser(
                "Некоторые файлы могут содержать настройки игры.\n" +
                "Их восстановление приведёт к сбросу настроек по умолчанию.\n\n" +
                FormatFileList(settings.Select(f => f.MainPath)) +
                "\n\nВернуть эти файлы к состоянию сборки?",
                ct).ConfigureAwait(false);
            if (!reset)
            {
                modified.RemoveAll(settings.Contains);
            }
        }

        var toDownload = modified.Concat(missing).ToList();
        if (toDownload.Count > 0)
        {
            var details = await services.Api.GetGameFiles(schema.Version, services.Region, ct).ConfigureAwait(false);
            var plan = ProtoVersion.Parser.ParseFrom(await services.Downloader
                .DownloadManifestAsync(details.Host, details.Files, details.CompressedFiles, ct).ConfigureAwait(false));
            var planByPath = plan.Files.ToDictionary(f => f.Path);
            var files = toDownload
                .Where(f => planByPath.ContainsKey(f.MainPath))
                .Select(f => new V2FilePlan(f.MainPath, f.HasOptionalPath ? f.OptionalPath : null, planByPath[f.MainPath].Storage, planByPath[f.MainPath].HasCompressedStorage ? planByPath[f.MainPath].CompressedStorage : null, f.Sha256.ToByteArray(), f.Compression, f.HasSize ? f.Size : null))
                .ToList();
            if (files.Count > 0)
            {
                await JoinAsync(new DownloadFilesV2Task(services), new V2DownloadParameters(details.Host, files, ApplyOptionalPath: true), ct).ConfigureAwait(false);
            }
        }

        // БТ-3.2/БТ-3.5: файлы вне сборки — перечисляем и удаляем только с согласия
        var known = CollectKnownPaths(schema);
        var strangers = EnumerateNonBuildFiles(core.Paths.Root, known);
        if (strangers.Count > 0 && AskUser != null)
        {
            var remove = await AskUser(
                "В папке игры есть файлы, не относящиеся к сборке.\n\n" +
                FormatFileList(strangers) +
                "\n\nУдалить их? Сохранения, скриншоты и журналы не затрагиваются.",
                ct).ConfigureAwait(false);
            if (remove)
            {
                foreach (var stranger in strangers)
                {
                    TryDelete(core.Paths.Resolve(stranger));
                }
            }
        }

        Report(100);
        Describe("Проверка состояния");
        await JoinAsync(new FillSchemaV2Task(services), null, ct).ConfigureAwait(false);

        // БТ-4.4: заново применяются только варианты, чьих файлов коснулась перекачка
        var affected = toDownload.Select(f => f.MainPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filesForApply = new List<(Option? Current, Option Target)>();
        foreach (var section in repository.GetSections().Where(s => s.Downloaded))
        {
            var applied = section.Options.FirstOrDefault(o => o.Applied);
            if (applied != null && applied.Files.Any(file => affected.Contains(file.GamePath)))
            {
                filesForApply.Add((null, applied));
            }
        }
        if (filesForApply.Count > 0)
        {
            await JoinAsync(new ApplyOptionsTask(services.Core), filesForApply, ct).ConfigureAwait(false);
        }
        return null;
    }

    private static bool IsSettingsFile(string mainPath)
    {
        var normalized = mainPath.Replace('\\', '/');
        return SettingsFiles.Any(s => normalized.Contains(s.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Все пути, которые клиент считает частью сборки: обязательные и опционные копии.</summary>
    private static HashSet<string> CollectKnownPaths(ProtoSchema schema)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // внешние партиции и файлы всех вариантов: пакеты и их пути установки — часть сборки
        foreach (var file in schema.Partitions.SelectMany(p => p.Files)
                     .Concat(schema.Options.SelectMany(o => o.Contents).SelectMany(c => c.Partition.Files)))
        {
            if (!string.IsNullOrEmpty(file.MainPath))
            {
                known.Add(file.MainPath.Replace('\\', '/'));
            }
            if (file.HasOptionalPath)
            {
                known.Add(file.OptionalPath.Replace('\\', '/'));
            }
        }
        foreach (var settings in SettingsFiles)
        {
            known.Add(settings.Replace('\\', '/'));
        }
        return known;
    }

    /// <summary>БТ-3.5: файл, которого нет ни в схеме, ни в настройках, ни в исключениях.</summary>
    private List<string> EnumerateNonBuildFiles(string root, HashSet<string> known)
    {
        var strangers = new List<string>();
        EnumerateDirectory(root, "");
        return strangers;

        void EnumerateDirectory(string absolute, string relativePrefix)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(absolute))
            {
                var name = Path.GetFileName(entry);
                var relative = relativePrefix.Length == 0 ? name : relativePrefix + "/" + name;
                if (IsExcluded(relative))
                {
                    continue;
                }
                if (Directory.Exists(entry))
                {
                    EnumerateDirectory(entry, relative);
                }
                else if (!known.Contains(relative))
                {
                    strangers.Add(relative);
                }
            }
        }
    }

    private static bool IsExcluded(string relativePath)
    {
        foreach (var excluded in ExcludedPaths)
        {
            if (relativePath.Equals(excluded, StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith(excluded + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string FormatFileList(IEnumerable<string> paths)
    {
        const int shown = 20;
        var list = paths.Take(shown).ToList();
        var text = string.Join("\n", list);
        var extra = paths.Count() - list.Count;
        if (extra > 0)
        {
            text += $"\n… и ещё {extra}";
        }
        return text;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
