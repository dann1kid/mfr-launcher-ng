using System.Diagnostics;
using Mfr.Core.Platform;

namespace Mfr.Platform.Linux;

/// <summary>
/// На Linux exe можно заменить in-place пока процесс ещё жив (unlink + rename).
/// UseShellExecute=false — не тянет System.IO.Pipes.
/// </summary>
public sealed class LinuxSelfUpdate : IPlatformSelfUpdate
{
    public void ReplaceAndRestart(string downloadedExePath)
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь клиента");

        // Linux позволяет перезаписать запущенный бинарник через rename
        File.Copy(downloadedExePath, executablePath, overwrite: true);

        // chmod +x
        Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/chmod",
            Arguments = $"+x \"{executablePath}\"",
            UseShellExecute = false,
        })?.WaitForExit(5000);

        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
        });
        Environment.Exit(0);
    }
}
