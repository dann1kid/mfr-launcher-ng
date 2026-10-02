using Mfr.Core.Storage;

namespace Mfr.Core.Tasks;

/// <summary>
/// Applies options: moves the current option's game files to temp backups,
/// copies the target option's storage files into place, rolls everything back
/// on failure (Kotlin: ApplyOptionsTask). Each file exists twice — a storage
/// copy (storagePath) and the active game copy (gamePath).
/// </summary>
public sealed class ApplyOptionsTask(LauncherServices services)
    : LauncherTask<IReadOnlyList<(Option? Current, Option Target)>, object?>(services)
{
    protected override Task<object?> Action(
        IReadOnlyList<(Option? Current, Option Target)> parameters, CancellationToken cancellationToken)
    {
        var failedOptions = new Dictionary<string, List<string>>();
        var totalCount = parameters.Sum(p => p.Target.Files.Count);
        var currentCount = 0L;

        foreach (var (currentOption, option) in parameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Describe($"Установка пакета {option.SectionName} - {option.Name}");

            var missing = option.Files
                .Where(file => !File.Exists(Services.Paths.Resolve(file.StoragePath)))
                .ToList();
            if (missing.Count > 0)
            {
                failedOptions[$"{option.SectionName} ({option.Name})"] = missing.Select(f => f.StoragePath).ToList();
                currentCount += option.Files.Count;
                Report(currentCount, totalCount);
                continue;
            }

            var backups = new List<(string GamePath, string TempPath)>();
            try
            {
                if (currentOption != null)
                {
                    foreach (var file in currentOption.Files)
                    {
                        var gamePath = Services.Paths.Resolve(file.GamePath);
                        if (File.Exists(gamePath))
                        {
                            var tempPath = Path.Combine(Path.GetTempPath(), $"MFR_{Guid.NewGuid():N}");
                            File.Move(gamePath, tempPath);
                            backups.Add((gamePath, tempPath));
                        }
                    }
                }

                foreach (var file in option.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var gamePath = Services.Paths.Resolve(file.GamePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(gamePath)!);
                    File.Copy(Services.Paths.Resolve(file.StoragePath), gamePath, overwrite: true);
                    currentCount++;
                    Report(currentCount, totalCount);
                }

                if (currentOption != null)
                {
                    Services.Repository.SetOptionApplied(currentOption.Id, false);
                }
                Services.Repository.SetOptionApplied(option.Id, true);
            }
            catch (Exception)
            {
                // remove partially copied files of the target option, then restore backups
                foreach (var file in option.Files)
                {
                    TryDelete(Services.Paths.Resolve(file.GamePath));
                }
                foreach (var (gamePath, tempPath) in backups)
                {
                    File.Move(tempPath, gamePath, overwrite: true);
                }
                throw new InvalidOperationException(
                    $"Ошибка применения пакета {option.SectionName} - {option.Name}");
            }
            finally
            {
                foreach (var (_, tempPath) in backups)
                {
                    TryDelete(tempPath);
                }
            }
        }

        if (failedOptions.Count > 0)
        {
            throw new InvalidOperationException(
                "Пакеты не применены (нет файлов в хранилище): " + string.Join(", ", failedOptions.Keys));
        }

        Report(100);
        return Task.FromResult<object?>(null);
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
