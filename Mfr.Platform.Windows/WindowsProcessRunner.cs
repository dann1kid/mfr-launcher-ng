using System.Diagnostics;
using Mfr.Core.Platform;

namespace Mfr.Platform.Windows;

/// <summary>
/// Запуск через powershell Start-Process: MCP требует UAC (-Verb runAs),
/// пути с [ ] экранируются backtick.
/// </summary>
public sealed class WindowsProcessRunner : IPlatformProcessRunner
{
    public Process StartApplication(string executablePath, string workingDirectory) =>
        StartViaPowerShell(executablePath, workingDirectory);

    public Process StartElevated(string executablePath, string workingDirectory) =>
        StartViaPowerShell(executablePath, workingDirectory, elevate: true);

    public Process StartManaged(string executablePath, string workingDirectory) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("Не удалось запустить процесс");

    private static Process StartViaPowerShell(string executable, string workingDirectory, bool elevate = false)
    {
        var command =
            $"Start-Process '{Escape(executable)}' " +
            $"-WorkingDirectory '{Escape(workingDirectory)}' {(elevate ? "-Verb runAs" : "")}".TrimEnd();
        return Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-Command \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("powershell не запустился");
    }

    private static string Escape(string path) => path.Replace("]", "`]").Replace("[", "`[");
}
