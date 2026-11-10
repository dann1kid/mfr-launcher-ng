using Mfr.Core.Platform;

namespace Mfr.Launcher;

/// <summary>
/// Выбор платформенных реализаций при старте (правило AGENTS.md:
/// ядро не ветвится по ОС — связывает их Launcher).
/// </summary>
public static class PlatformServices
{
    public static IPlatformProcessRunner ProcessRunner { get; } = CreateProcessRunner();
    public static IPlatformSelfUpdate SelfUpdate { get; } = CreateSelfUpdate();
    public static IPlatformSingleInstance CreateSingleInstance(string folderTag) =>
        OperatingSystem.IsWindows()
            ? new Mfr.Platform.Windows.WindowsSingleInstance(folderTag)
            : new Mfr.Platform.Linux.LinuxSingleInstance(folderTag);

    private static IPlatformProcessRunner CreateProcessRunner() =>
        OperatingSystem.IsWindows()
            ? new Mfr.Platform.Windows.WindowsProcessRunner()
            : new Mfr.Platform.Linux.LinuxProcessRunner();

    private static IPlatformSelfUpdate CreateSelfUpdate() =>
        OperatingSystem.IsWindows()
            ? new Mfr.Platform.Windows.WindowsSelfUpdate()
            : new Mfr.Platform.Linux.LinuxSelfUpdate();
}
