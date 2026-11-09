using System.Diagnostics;
using System.Net;

using Mfr.Core.Tasks;

namespace Mfr.Core.V2;

/// <summary>
/// Сравнение версий по числовым октетам (БТ-9, правило 2 AGENTS): обновление предлагается,
/// только когда версия сервера СТРОГО больше своей. Неразборчивая строка не предлагается.
/// </summary>
public static class LauncherVersions
{
    public static bool IsStrictlyGreater(string? server, string? current)
    {
        if (TryParse(server, out var serverParts) && TryParse(current, out var currentParts))
        {
            for (var i = 0; i < Math.Max(serverParts.Length, currentParts.Length); i++)
            {
                var serverPart = i < serverParts.Length ? serverParts[i] : 0;
                var currentPart = i < currentParts.Length ? currentParts[i] : 0;
                if (serverPart != currentPart)
                {
                    return serverPart > currentPart;
                }
            }
        }
        return false;
    }

    private static bool TryParse(string? version, out int[] parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }
        var pieces = version.Split('.');
        if (pieces.Length < 2)
        {
            return false;
        }
        var parsed = new int[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
        {
            if (!int.TryParse(pieces[i], out parsed[i]) || parsed[i] < 0)
            {
                return false;
            }
        }
        parts = parsed;
        return true;
    }
}

/// <summary>
/// Самообновление клиента (БТ-9): канал /v2/launcher/*, скачивание объекта выкладки,
/// проверка загруженного файла до замены и замена своего exe с перезапуском.
/// </summary>
public sealed class LauncherUpdateV2Task(LauncherServicesV2 services) : LauncherTask<object?, object?>(services.Core)
{
    protected override async Task<object?> Action(object? parameters, CancellationToken ct)
    {
        Describe("Подготовка к обновлению клиента");
        var version = await services.Api.GetLauncherVersion(services.Region, ct).ConfigureAwait(false);
        var files = await services.Api.GetLauncherFiles(version, services.Region, ct).ConfigureAwait(false);

        var tempFile = Path.Combine(Path.GetTempPath(), $"MFR_launcher_update_{Guid.NewGuid():N}");
        try
        {
            Describe("Скачивание обновления");
            await services.Downloader.DownloadToFileAsync(files.Host, files.LauncherStorage, tempFile, ct).ConfigureAwait(false);

            Describe("Проверка обновления");
            Report(90);
            if (!IsExecutableFile(tempFile))
            {
                throw new InvalidOperationException("Скачанный файл клиента повреждён");
            }

            Describe("Установка обновления");
            Report(100);
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Не удалось определить путь клиента");
            var oldPath = executablePath + ".old";
            try
            {
                File.Delete(oldPath);
            }
            catch (IOException)
            {
            }
            File.Move(executablePath, oldPath);
            try
            {
                File.Copy(tempFile, executablePath);
            }
            catch (Exception)
            {
                File.Move(oldPath, executablePath, overwrite: true);
                throw;
            }
            Process.Start(new ProcessStartInfo { FileName = executablePath, UseShellExecute = true });
            Environment.Exit(0);
            return null;
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

    /// <summary>Минимальная проверка до замены: это исполняемый файл Windows (MZ), не пустой и не HTML-заглушка.</summary>
    private static bool IsExecutableFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 1024)
            {
                return false;
            }
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (IOException)
        {
            return false;
        }
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
