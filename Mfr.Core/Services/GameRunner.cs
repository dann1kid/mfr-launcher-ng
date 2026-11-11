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

    /// <summary>
    /// БТ-1.3: запустить настройщик MGE XE ради прописки в реестре и через несколько
    /// секунд закрыть. Настройщика в сборке нет или платформа его не подняла —
    /// не повод ронять установку: возвращаемся без ошибки.
    /// </summary>
    public async Task RunMgeConfiguratorAsync(TimeSpan closeAfter, CancellationToken ct)
    {
        Process mge;
        try
        {
            mge = StartMge();
        }
        catch (Exception)
        {
            return;
        }
        using (mge)
        {
            await Task.Delay(closeAfter, ct).ConfigureAwait(false);
            if (!mge.HasExited)
            {
                mge.Kill();
            }
        }
    }
}
