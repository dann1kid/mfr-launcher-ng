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
    /// Скачанные файлы не имеют бита +x: выставляем перед запуском.
    /// У официального Linux-пакета OpenMW запуск идёт через скрипт-обёртку,
    /// которая выполняет соседний &lt;имя&gt;.x86_64 — ему тоже нужен бит.
    /// </summary>
    private static void MakeExecutable(string executablePath)
    {
        TrySetExecute(executablePath);
        TrySetExecute(executablePath + ".x86_64");
    }

    private static void TrySetExecute(string path)
    {
        // для анализатора: UnixFileMode недоступен на Windows, а проект собирается
        // на всех ОС; в рантайме раннер выбирается только вне Windows
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            var info = new FileInfo(path);
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
