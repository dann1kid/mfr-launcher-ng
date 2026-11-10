using System.Diagnostics;
using Mfr.Core.Platform;

namespace Mfr.Core.Services;

/// <summary>
/// Запуск игры и её утилит через платформенную реализацию:
/// Windows — powershell Start-Process (UAC для MCP), Linux — прямой exec.
/// Платформа выбирается в Launcher.PlatformServices.
/// </summary>
public sealed class GameRunner(GamePaths paths, IPlatformProcessRunner runner)
{
    public void StartClassicGame() => runner.StartApplication(paths.ClassicApplication, paths.Root);
    public void StartClassicLauncher() => runner.StartApplication(paths.ClassicLauncher, paths.Root);
    public void StartMcp() => runner.StartElevated(paths.McpApplication, paths.Root);
    public void StartOpenMwGame() => runner.StartApplication(paths.OpenMwApplication, paths.Root);
    public void StartOpenMwLauncher() => runner.StartApplication(paths.OpenMwLauncher, paths.Root);

    public Process StartMge() => runner.StartManaged(paths.MgeApplication, paths.Root);
}
