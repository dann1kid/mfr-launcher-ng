using System.Globalization;
using Mfr.Core.Storage;
using Mfr.Protocol.Cryptography;
using Mfr.Protocol.Dto;
using Mfr.Protocol.Enums;

namespace Mfr.Core.Tasks;

/// <summary>
/// Integrity check (Kotlin: CheckGameConsistencyTask): deletes files marked
/// inactive in the manifest, MD5-verifies MAIN plus downloaded OPTIONAL/EXTRA
/// files, asks the user before overwriting settings files, re-downloads the
/// broken ones, then refreshes the schema and re-applies applied options.
/// </summary>
public sealed class CheckConsistencyTask(LauncherServices services) : LauncherTask<object?, object?>(services)
{
    /// <summary>Set by the caller; the old client showed a modal QuestionController.</summary>
    public Func<string, CancellationToken, Task<bool>>? AskUser { get; init; }

    public int BuildId { get; init; }

    private static readonly string[] SettingsFiles =
    [
        "Morrowind.exe",
        "Morrowind.ini",
        @"mge3\MGE.ini",
        @"Data Files\MWSE\config",
        @"mcpatch\installed",
    ];

    protected override async Task<object?> Action(object? parameters, CancellationToken cancellationToken)
    {
        Describe("Проверка целостности игры");
        var startDate = DateTime.UtcNow;

        var content = await Services.Api.GetGameContent(BuildId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var manifestFiles = content.Categories.SelectMany(c => c.Items).SelectMany(i => i.Files).ToList();

        foreach (var file in manifestFiles.Where(f => !f.Active))
        {
            TryDelete(Services.Paths.Resolve(file.Path));
        }

        var downloadedSections = Services.Repository.GetSections().Where(s => s.Downloaded).Select(s => s.Name).ToHashSet();
        var downloadedExtras = Services.Repository.GetExtras().Where(e => e.Downloaded).Select(e => e.Name).ToHashSet();

        var filesForCheck = content.Categories.First(c => c.Type == ContentType.MAIN)
            .Items.SelectMany(i => i.Files)
            .ToList();
        var optional = content.Categories.FirstOrDefault(c => c.Type == ContentType.OPTIONAL);
        if (optional != null)
        {
            filesForCheck.AddRange(optional.Items
                .Where(i => downloadedSections.Contains(i.Name))
                .SelectMany(i => i.Files));
        }
        var extra = content.Categories.FirstOrDefault(c => c.Type == ContentType.EXTRA);
        if (extra != null)
        {
            filesForCheck.AddRange(extra.Items
                .Where(i => downloadedExtras.Contains(i.Name))
                .SelectMany(i => i.Files));
        }

        var totalCount = filesForCheck.Count;
        var currentCount = 0;
        var brokenFiles = new List<FileDto>();
        foreach (var file in filesForCheck)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(++currentCount, totalCount);
            var path = Services.Paths.Resolve(file.Path);
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                if (Md5.Hash(stream).AsSpan().SequenceEqual(file.Md5))
                {
                    continue;
                }
            }
            brokenFiles.Add(file);
        }

        var settingsFiles = brokenFiles
            .Where(f => SettingsFiles.Any(s => f.Path.Contains(s, StringComparison.Ordinal)))
            .ToList();
        if (settingsFiles.Count > 0 && AskUser != null)
        {
            var reset = await AskUser(
                """
                Некоторые файлы могут содержать настройки игры.
                Их восстановление приведет к восстановлению настроек по умолчанию.
                Хотите сбросить настройки?
                """,
                cancellationToken).ConfigureAwait(false);
            if (!reset)
            {
                brokenFiles.RemoveAll(settingsFiles.Contains);
            }
        }

        if (brokenFiles.Count > 0)
        {
            foreach (var file in brokenFiles)
            {
                TryDelete(Services.Paths.Resolve(file.Path));
            }

            var activeBroken = brokenFiles.Where(f => f.Active).ToList();
            if (activeBroken.Count > 0)
            {
                await JoinAsync(new DownloadFilesTask(Services), activeBroken, cancellationToken).ConfigureAwait(false);
            }
        }

        Services.Repository.SetProperty(PropertyKeys.LastUpdateDate, InstallGameTask.Format(startDate));

        Report(100);
        Describe("Проверка состояния");

        await JoinAsync(new FillSchemaTask(Services), null, cancellationToken).ConfigureAwait(false);

        var filesForApply = new List<(Option? Current, Option Target)>();
        foreach (var section in Services.Repository.GetSections().Where(s => s.Downloaded))
        {
            var applied = section.Options.FirstOrDefault(o => o.Applied);
            if (applied != null)
            {
                filesForApply.Add((null, applied));
            }
        }
        if (filesForApply.Count > 0)
        {
            await JoinAsync(new ApplyOptionsTask(Services), filesForApply, cancellationToken).ConfigureAwait(false);
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
