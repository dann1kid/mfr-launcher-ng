using System.Diagnostics;
using Mfr.Core.Platform;

namespace Mfr.Platform.Linux;

/// <summary>
/// Прямой запуск: на Linux не нужен powershell, UseShellExecute=true тянет
/// System.IO.Pipes (не попадает в single-file bundle). Бит +x выставляется
/// перед запуском — скачанные из манифеста файлы его не имеют.
/// </summary>
public sealed class LinuxProcessRunner : IPlatformProcessRunner
{
    public Process StartApplication(string executablePath, string workingDirectory) =>
        Start(executablePath, workingDirectory);

    public Process StartElevated(string executablePath, string workingDirectory)
    {
        // pkexec для графического запроса прав, фолбэк на прямое повышение через sudo
        var psi = new ProcessStartInfo
        {
            FileName = "pkexec",
            Arguments = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        return Process.Start(psi)
               ?? throw new InvalidOperationException("pkexec не запустился");
    }

    public Process StartManaged(string executablePath, string workingDirectory) =>
        Start(executablePath, workingDirectory);

    private static Process Start(string executablePath, string workingDirectory)
    {
        MakeExecutable(executablePath);
        return Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"Не удалось запустить {executablePath}");
    }

    /// <summary>
    /// Скачанные файлы не имеют бита +x: выставляем перед запуском
    /// (бинарники OpenMW, AppImage — все приходят из манифеста).
    /// </summary>
    private static void MakeExecutable(string executablePath)
    {
        try
        {
            var info = new FileInfo(executablePath);
            if (info.Exists && (info.UnixFileMode & System.IO.UnixFileMode.UserExecute) == 0)
            {
                info.UnixFileMode |= System.IO.UnixFileMode.UserExecute
                    | System.IO.UnixFileMode.GroupExecute
                    | System.IO.UnixFileMode.OtherExecute;
            }
        }
        catch (Exception)
        {
            // best effort: без бита exec сам скажет Permission denied с понятной причиной
        }
    }
}
