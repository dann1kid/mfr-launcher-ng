using System.Diagnostics;

namespace Mfr.Core.Services;

/// <summary>
/// Starts the game and its tools the same way the old WindowsRunnerService
/// does: via powershell Start-Process (MCP needs UAC elevation with
/// -Verb runAs); '[' and ']' in paths are escaped with backticks. MGE is
/// started directly so the launcher can close it after first install.
/// </summary>
public sealed class GameRunner(GamePaths paths)
{
    public void StartClassicGame() => StartViaPowerShell(paths.ClassicApplication);
    public void StartClassicLauncher() => StartViaPowerShell(paths.ClassicLauncher);
    public void StartMcp() => StartViaPowerShell(paths.McpApplication, elevate: true);
    public void StartOpenMwGame() => StartViaPowerShell(paths.OpenMwApplication);
    public void StartOpenMwLauncher() => StartViaPowerShell(paths.OpenMwLauncher);

    public Process StartMge() => Process.Start(new ProcessStartInfo
    {
        FileName = paths.MgeApplication,
        WorkingDirectory = paths.Root,
        UseShellExecute = true,
    }) ?? throw new InvalidOperationException("Не удалось запустить MGE");

    private void StartViaPowerShell(string executable, bool elevate = false)
    {
        var command =
            $"Start-Process '{Escape(executable)}' " +
            $"-WorkingDirectory '{Escape(paths.Root)}' {(elevate ? "-Verb runAs" : "")}".TrimEnd();
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-Command \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    private static string Escape(string path) => path.Replace("]", "`]").Replace("[", "`[");
}
