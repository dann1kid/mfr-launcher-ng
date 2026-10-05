using Mfr.Core.Exceptions;
using Mfr.Core.Services;
using Mfr.Core.Storage;
using Mfr.Protocol.Dto;
using Mfr.Protocol.Enums;

namespace Mfr.Core.Tasks;

/// <summary>
/// Full game install (Kotlin: GameInstallTask): free-space check (×1.1),
/// download of active MAIN files minus files owned by applied options,
/// schema/consistency pass, default MIDDLE configs, and a 4-second MGE run.
/// </summary>
public sealed class InstallGameTask(LauncherServices services) : LauncherTask<int, object?>(services)
{
    protected override async Task<object?> Action(int buildId, CancellationToken cancellationToken)
    {
        Describe("Подготовка к установке");
        var startDateTime = DateTime.UtcNow;

        var content = await Services.Api.GetGameContent(buildId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var main = content.Categories.First(c => c.Type == ContentType.MAIN);

        CheckFreeSpace(main.Items.SelectMany(i => i.Files).Sum(f => f.Size));

        var optionFiles = Services.Repository.GetSections()
            .SelectMany(s => s.Options)
            .Where(o => o.Applied)
            .SelectMany(o => o.Files)
            .Select(f => f.GamePath)
            .ToHashSet(StringComparer.Ordinal);

        var files = main.Items
            .SelectMany(i => i.Files)
            .Where(f => f.Active && !optionFiles.Contains(f.Path))
            .ToList();

        var downloadTask = new DownloadFilesTask(Services);
        await JoinAsync(downloadTask, files, cancellationToken).ConfigureAwait(false);

        // self-check: a dropped connection can end the download "successfully"
        // with incomplete files; re-download whatever does not match the manifest
        var broken = files
            .Where(f => !System.IO.File.Exists(Services.Paths.Resolve(f.Path)) || !Md5Matches(f))
            .ToList();
        if (broken.Count > 0)
        {
            await JoinAsync(new DownloadFilesTask(Services), broken, cancellationToken).ConfigureAwait(false);
        }

        Report(100);
        Describe("Анализ схемы");
        if (optionFiles.Count > 0)
        {
            var consistency = new CheckConsistencyTask(Services) { BuildId = buildId };
            await JoinAsync(consistency, null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await JoinAsync(new FillSchemaTask(Services), null, cancellationToken).ConfigureAwait(false);
        }

        Services.Repository.SetProperty(PropertyKeys.LastUpdateDate, Format(startDateTime));
        Services.Repository.SetProperty(PropertyKeys.GameInstalled, "true");
        Mfr.Core.Services.ModifyFiles.ApplyLoadOrderDates(Services.Paths.Root);

        Services.Mge.ApplyConfig(MgeConfiguration.MIDDLE, backup: false);
        Services.OpenMw.PrepareTemplates();
        Services.OpenMw.ApplyConfig(OpenMwConfiguration.MIDDLE, backup: false);

        using var mge = Services.Runner.StartMge();
        await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
        if (!mge.HasExited)
        {
            mge.Kill();
        }
        return null;
    }

    internal void CheckFreeSpace(long bytesNeeded)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Services.Paths.Root)!);
        var freeKb = drive.AvailableFreeSpace / 1024;
        var needKb = (long)(bytesNeeded * 1.1 / 1024);
        if (freeKb < needKb)
        {
            throw new NotEnoughSpaceException(
                $"Для успешной установки необходимо минимум {needKb} КБ свободного места. " +
                $"Доступно всего {freeKb} КБ. Установка будет прервана");
        }
    }

    internal static string Format(DateTime dateTime) => dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss");

    private bool Md5Matches(Mfr.Protocol.Dto.FileDto file)
    {
        using var stream = System.IO.File.OpenRead(Services.Paths.Resolve(file.Path));
        return Mfr.Protocol.Cryptography.Md5.Hash(stream).AsSpan().SequenceEqual(file.Md5);
    }
}
