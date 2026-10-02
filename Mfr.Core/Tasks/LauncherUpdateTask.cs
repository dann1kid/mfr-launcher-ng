using System.Diagnostics;
using Mfr.Core.Network;
using Mfr.Protocol.Cryptography;
using Mfr.Protocol.Enums;

namespace Mfr.Core.Tasks;

/// <summary>
/// Launcher self-update (Kotlin: LauncherUpdateTask + updater jar): downloads
/// the platform binary over the TCP protocol, verifies MD5 and hands over to
/// Mfr.Updater.exe which waits for this process to exit, replaces the
/// executable and restarts it (no more bundled JRE chain).
/// </summary>
public sealed class LauncherUpdateTask(LauncherServices services) : LauncherTask<object?, object?>(services)
{
    protected override async Task<object?> Action(object? parameters, CancellationToken cancellationToken)
    {
        Describe("Подготовка к скачиванию");
        var versions = await Services.Api.GetLauncherVersions(cancellationToken).ConfigureAwait(false);
        var target = versions.FirstOrDefault(v => v.System == Services.Options.Platform)
            ?? throw new InvalidOperationException($"Платформа {Services.Options.Platform} не найдена на сервере");

        var tempFile = Path.Combine(Path.GetTempPath(), $"MFR_launcher_update_{Guid.NewGuid():N}");
        try
        {
            await Services.Downloader.DownloadLauncher(
                FileRequest.Launcher(tempFile, target.Size, target.Md5), cancellationToken).ConfigureAwait(false);

            Describe("Подготовка к установке");
            Report(100);

            using (var stream = File.OpenRead(tempFile))
            {
                if (!Md5.Hash(stream).AsSpan().SequenceEqual(target.Md5))
                {
                    throw new InvalidOperationException("Скачанный файл лончера повреждён (MD5 не совпал)");
                }
            }

            // Windows allows renaming a running executable: swap ourselves in place
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Не удалось определить путь лаунчера");
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
                File.Delete(tempFile);
            }
            catch (Exception)
            {
                // restore the previous executable rather than leaving a broken install
                File.Move(oldPath, executablePath, overwrite: true);
                throw;
            }
            Process.Start(new ProcessStartInfo { FileName = executablePath, UseShellExecute = true });
            Environment.Exit(0);
            return null;
        }
        catch
        {
            TryDelete(tempFile);
            throw;
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
