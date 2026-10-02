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
internal static class V2Manifests
{
    public const string SchemaFileName = "schema.proto";

    public static async Task<(ProtoSchema Schema, ProtoVersion Plan, string Host)> LoadAsync(
        LauncherServicesV2 services, Region region, string line, CancellationToken ct)
    {
        var version = await services.Api.GetGameVersion(line, region, ct).ConfigureAwait(false);
        if (version.State != LineVersionState.Found || version.Id is null)
        {
            throw new ServerMaintenanceException();
        }
        var details = await services.Api.GetGameFiles(version.Id, region, ct).ConfigureAwait(false);
        var schemaBytes = await services.Downloader.DownloadManifestAsync(details.Host, details.Schema, details.CompressedSchema, ct).ConfigureAwait(false);
        var planBytes = await services.Downloader.DownloadManifestAsync(details.Host, details.Files, details.CompressedFiles, ct).ConfigureAwait(false);
        var schema = ProtoSchema.Parser.ParseFrom(schemaBytes);
        var plan = ProtoVersion.Parser.ParseFrom(planBytes);
        return (schema, plan, details.Host);
    }

    /// <summary>Every file the launcher is responsible for: required partitions and required option contents.</summary>
    public static IEnumerable<SchemaFile> MandatoryFiles(ProtoSchema schema) =>
        schema.Partitions.Where(p => p.Required).SelectMany(p => p.Files)
            .Concat(schema.Options.SelectMany(o => o.Contents)
                .Where(c => c.Partition.Required)
                .SelectMany(c => c.Partition.Files));

    /// <summary>Files of downloaded options plus everything mandatory (Kotlin: findContent scope).</summary>
    public static IEnumerable<SchemaFile> ActiveFiles(ProtoSchema schema, IReadOnlyCollection<string> downloadedOptions)
    {
        foreach (var file in MandatoryFiles(schema))
        {
            yield return file;
        }
        foreach (var file in schema.Options
            .SelectMany(o => o.Contents)
            .Where(c => !c.Partition.Required && downloadedOptions.Contains(c.Name))
            .SelectMany(c => c.Partition.Files))
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
/// v2 install (Kotlin: GameInstallTask 3.3.x): picks a compatibility line,
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

        // one-time cleanup when upgrading from a pre-line release
        if (repository.GetProperty(PropertyKeys.GameInstalled) is not null &&
            repository.GetProperty(PropertyKeys.OldReleaseRemoved) is null)
        {
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

        var region = services.Region;
        var line = repository.GetProperty(PropertyKeys.SelectedBuild)
            ?? (await services.Api.GetChannels(region, ct).ConfigureAwait(false)).FirstOrDefault()
            ?? throw new ServerMaintenanceException();

        var (schema, plan, host) = await V2Manifests.LoadAsync(services, region, line, ct).ConfigureAwait(false);
        V2Manifests.SaveSchemaFile(core, schema);
        repository.SetProperty(PropertyKeys.SelectedBuild, line);

        var planByPath = plan.Files.ToDictionary(f => f.Path);
        var mandatory = V2Manifests.MandatoryFiles(schema)
            .Where(f => f.MainPath is not null)
            .ToList();
        if (mandatory.Any(f => !planByPath.ContainsKey(f.MainPath)))
        {
            throw new InvalidOperationException("Inconsistent files");
        }

        var files = mandatory.Select(f => new V2FilePlan(
            f.MainPath,
            f.HasOptionalPath ? f.OptionalPath : null,
            planByPath[f.MainPath].Storage,
            planByPath[f.MainPath].HasCompressedStorage ? planByPath[f.MainPath].CompressedStorage : null,
            f.Sha256.ToByteArray())).ToList();

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

        using var mge = Services.Runner.StartMge();
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
/// v2 update (Kotlin: GameUpdateTask 3.3.x): diffs the installed schema.proto
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

        var downloadedOptions = repository.GetSections()
            .Where(s => s.Downloaded)
            .SelectMany(s => s.Options.Select(o => o.Name))
            .ToHashSet();

        var currentFiles = V2Manifests.ActiveFiles(current, downloadedOptions)
            .Where(f => f.MainPath is not null)
            .GroupBy(f => f.MainPath)
            .ToDictionary(g => g.Key, g => g.First());
        var newFiles = V2Manifests.ActiveFiles(schema, downloadedOptions)
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
                    f.Sha256.ToByteArray());
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

        var filesForApply = new List<(Option? Current, Option Target)>();
        foreach (var section in repository.GetSections().Where(s => s.Downloaded))
        {
            var applied = section.Options.FirstOrDefault(o => o.Applied);
            if (applied != null)
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
/// option by SHA-256 (Kotlin: FillSchemeTask over the new schema).
/// </summary>
public sealed class FillSchemaV2Task(LauncherServicesV2 services) : LauncherTask<object?, object?>(services.Core)
{
    protected override Task<object?> Action(object? parameters, CancellationToken ct)
    {
        Describe("Обновление настраиваемых компонентов");
        var schema = V2Manifests.TryLoadLocalSchema(Services)
            ?? throw new ArgumentException("Файл схемы не найден: schema.proto");

        var schemaHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(Path.Combine(Services.Paths.Root, V2Manifests.SchemaFileName))));
        if (Services.Repository.GetProperty(PropertyKeys.Schema) == schemaHash)
        {
            return Task.FromResult<object?>(null);
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
        if (notApplied.Count > 0)
        {
            Describe("Поиск активной конфигурации");
            var total = notApplied.Sum(s => s.Options.Sum(o => o.Files.Count));
            var current = 0L;
            foreach (var section in notApplied)
            {
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
                        section.Options[index] = option with { Applied = true };
                        break;
                    }
                }
            }
        }

        Report(100);
        Describe("Сохранение настроек");
        Services.Repository.ReplaceSchema(rebuilt);
        Services.Repository.SetProperty(PropertyKeys.Schema, schemaHash);
        return Task.FromResult<object?>(null);
    }
}

/// <summary>
/// v2 consistency check: verifies active files by SHA-256, re-downloads the
/// broken ones, refreshes the schema and re-applies applied options
/// (Kotlin: CheckGameConsistencyTask 3.3.x, SHA-256 edition).
/// </summary>
public sealed class CheckConsistencyV2Task(LauncherServicesV2 services) : LauncherTask<object?, object?>(services.Core)
{
    public Func<string, CancellationToken, Task<bool>>? AskUser { get; init; }

    private static readonly string[] SettingsFiles =
    [
        "Morrowind.exe",
        "Morrowind.ini",
        @"mge3\MGE.ini",
        @"Data Files\MWSE\config",
        @"mcpatch\installed",
    ];

    protected override async Task<object?> Action(object? parameters, CancellationToken ct)
    {
        Describe("Проверка целостности игры");
        var core = Services;
        var repository = core.Repository;

        var schema = V2Manifests.TryLoadLocalSchema(core)
            ?? throw new ArgumentException("Файл схемы не найден: schema.proto");
        var downloadedOptions = repository.GetSections()
            .Where(s => s.Downloaded)
            .SelectMany(s => s.Options.Select(o => o.Name))
            .ToHashSet();
        var active = V2Manifests.ActiveFiles(schema, downloadedOptions)
            .Where(f => f.MainPath is not null)
            .GroupBy(f => f.MainPath)
            .Select(g => g.First())
            .ToList();

        var total = active.Count;
        var current = 0;
        var broken = new List<SchemaFile>();
        foreach (var file in active)
        {
            ct.ThrowIfCancellationRequested();
            Report(++current, total);
            var mainPath = core.Paths.Resolve(file.MainPath);
            if (File.Exists(mainPath) && Sha256.Matches(mainPath, file.Sha256.ToByteArray()))
            {
                continue;
            }
            broken.Add(file);
        }

        var settings = broken.Where(f => SettingsFiles.Any(s => f.MainPath.Contains(s, StringComparison.Ordinal))).ToList();
        if (settings.Count > 0 && AskUser != null)
        {
            var reset = await AskUser(
                """
                Некоторые файлы могут содержать настройки игры.
                Их восстановление приведет к восстановлению настроек по умолчанию.
                Хотите сбросить настройки?
                """,
                ct).ConfigureAwait(false);
            if (!reset)
            {
                broken.RemoveAll(settings.Contains);
            }
        }

        if (broken.Count > 0)
        {
            var details = await services.Api.GetGameFiles(
                schema.Version, services.Region, ct).ConfigureAwait(false);
            var plan = ProtoVersion.Parser.ParseFrom(await services.Downloader
                .DownloadManifestAsync(details.Host, details.Files, details.CompressedFiles, ct).ConfigureAwait(false));
            var planByPath = plan.Files.ToDictionary(f => f.Path);
            var files = broken
                .Where(f => planByPath.ContainsKey(f.MainPath))
                .Select(f => new V2FilePlan(
                    f.MainPath,
                    f.HasOptionalPath ? f.OptionalPath : null,
                    planByPath[f.MainPath].Storage,
                    planByPath[f.MainPath].HasCompressedStorage ? planByPath[f.MainPath].CompressedStorage : null,
                    f.Sha256.ToByteArray()))
                .ToList();
            if (files.Count > 0)
            {
                await JoinAsync(new DownloadFilesV2Task(services), new V2DownloadParameters(details.Host, files, ApplyOptionalPath: true), ct).ConfigureAwait(false);
            }
        }

        Report(100);
        Describe("Проверка состояния");
        await JoinAsync(new FillSchemaV2Task(services), null, ct).ConfigureAwait(false);

        var filesForApply = new List<(Option? Current, Option Target)>();
        foreach (var section in repository.GetSections().Where(s => s.Downloaded))
        {
            var applied = section.Options.FirstOrDefault(o => o.Applied);
            if (applied != null)
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
}
