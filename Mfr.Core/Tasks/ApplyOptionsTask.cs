using Mfr.Core.Storage;

namespace Mfr.Core.Tasks;

/// <summary>
/// Applies options: moves the current option's game files to temp backups,
/// copies the target option's storage files into place, rolls everything back
/// on failure. Each file exists twice — a storage
/// copy (storagePath) and the active game copy (gamePath).
/// </summary>
public sealed class ApplyOptionsTask(LauncherServices services)
    : LauncherTask<IReadOnlyList<(Option? Current, Option Target)>, object?>(services)
{
    protected override Task<object?> Action(
        IReadOnlyList<(Option? Current, Option Target)> parameters, CancellationToken cancellationToken)
    {
        var failedOptions = new Dictionary<string, List<string>>();
        // БТ-1/прогресс: полоса применения — доля записанного объёма, не числа файлов:
        // тысяча мелких файлов не должна прыгать процентами на одном крупном
        long StorageSize(OptionFile file)
        {
            var source = new FileInfo(Services.Paths.Resolve(file.StoragePath));
            return source.Exists ? source.Length : 0;
        }
        var totalBytes = parameters.Sum(p => p.Target.Files.Sum(StorageSize));
        var currentBytes = 0L;

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
                currentBytes += option.Files.Sum(StorageSize);
                Report(currentBytes, totalBytes);
                continue;
            }

            var backups = new List<(string GamePath, string TempPath)>();
            List<string> written = [];
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

                written = [];
                foreach (var file in option.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var gamePath = Services.Paths.Resolve(file.GamePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(gamePath)!);
                    File.Copy(Services.Paths.Resolve(file.StoragePath), gamePath, overwrite: true);
                    written.Add(gamePath);
                    currentBytes += StorageSize(file);
                    Report(currentBytes, totalBytes);
                }

                if (currentOption != null)
                {
                    Services.Repository.SetOptionApplied(currentOption.Id, false);
                }
                Services.Repository.SetOptionApplied(option.Id, true);
            }
            catch (Exception)
            {
                // БТ-4.3: откат трогает только файлы, до которых дошло применение
                foreach (var gamePath in written)
                {
                    TryDelete(gamePath);
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
