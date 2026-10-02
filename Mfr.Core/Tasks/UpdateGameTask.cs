using System.Globalization;
using Mfr.Core.Exceptions;
using Mfr.Core.Storage;
using Mfr.Protocol.Dto;
using Mfr.Protocol.Enums;

namespace Mfr.Core.Tasks;

/// <summary>
/// Incremental game update (Kotlin: GameUpdateTask): asks the server for files
/// changed after the stored LAST_UPDATE_DATE across MAIN plus downloaded
/// OPTIONAL sections and EXTRA packs, backs up every replaced file to temp and
/// restores the backups if the download fails (unlike the old client, the
/// failure is not swallowed — the update date is only bumped on success).
/// </summary>
public sealed class UpdateGameTask(LauncherServices services) : LauncherTask<int, object?>(services)
{
    protected override async Task<object?> Action(int buildId, CancellationToken cancellationToken)
    {
        Describe("Подготовка к обновлению");
        var startTime = DateTime.UtcNow;

        var files = await FindChangedFiles(buildId, cancellationToken).ConfigureAwait(false);
        if (files.Count > 0)
        {
            CheckFreeSpace(files.Sum(f => f.Size));

            var backups = new List<(string TargetPath, string TempPath)>();
            try
            {
                foreach (var file in files)
                {
                    var targetPath = Services.Paths.Resolve(file.Path);
                    if (File.Exists(targetPath))
                    {
                        var tempPath = Path.Combine(Path.GetTempPath(), $"MFR_update_{Guid.NewGuid():N}");
                        File.Move(targetPath, tempPath);
                        backups.Add((targetPath, tempPath));
                    }
                }

                await JoinAsync(new DownloadFilesTask(Services), files, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                foreach (var (targetPath, tempPath) in backups)
                {
                    if (File.Exists(tempPath))
                    {
                        File.Move(tempPath, targetPath, overwrite: true);
                    }
                }
                throw;
            }
            finally
            {
                foreach (var (_, tempPath) in backups)
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
            }
        }

        Report(0);
        Describe("Проверка состояния");
        Services.Repository.SetProperty(PropertyKeys.LastUpdateDate, InstallGameTask.Format(startTime));

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

    internal void CheckFreeSpace(long bytesNeeded)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Services.Paths.Root)!);
        var freeKb = drive.AvailableFreeSpace / 1024;
        var needKb = (long)(bytesNeeded * 1.1 / 1024);
        if (freeKb < needKb)
        {
            throw new NotEnoughSpaceException(
                $"Для успешного обновления необходимо минимум {needKb} КБ свободного места. " +
                $"Доступно всего {freeKb} КБ. Обновление будет прервано");
        }
    }

    private async Task<List<FileDto>> FindChangedFiles(int buildId, CancellationToken cancellationToken)
    {
        var stored = Services.Repository.GetProperty(PropertyKeys.LastUpdateDate);
        if (stored is null || !DateTime.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastUpdate))
        {
            return [];
        }

        var content = await Services.Api.GetGameContent(buildId, lastUpdate, cancellationToken).ConfigureAwait(false);
        var downloadedSections = Services.Repository.GetSections().Where(s => s.Downloaded).Select(s => s.Name).ToHashSet();
        var downloadedExtras = Services.Repository.GetExtras().Where(e => e.Downloaded).Select(e => e.Name).ToHashSet();

        // a filtered response with no changes has no categories at all
        var main = content.Categories.FirstOrDefault(c => c.Type == ContentType.MAIN);
        var changed = main?.Items.SelectMany(i => i.Files).ToList() ?? [];

        var optional = content.Categories.FirstOrDefault(c => c.Type == ContentType.OPTIONAL);
        if (optional != null)
        {
            changed.AddRange(optional.Items
                .Where(i => downloadedSections.Contains(i.Name))
                .SelectMany(i => i.Files));
        }

        var extra = content.Categories.FirstOrDefault(c => c.Type == ContentType.EXTRA);
        if (extra != null)
        {
            changed.AddRange(extra.Items
                .Where(i => downloadedExtras.Contains(i.Name))
                .SelectMany(i => i.Files));
        }
        return changed;
    }
}
