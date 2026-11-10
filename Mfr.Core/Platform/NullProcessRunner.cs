using System.Diagnostics;

namespace Mfr.Core.Platform;

/// <summary>Заглушка для тестов и офлайн-запуска без платформенной реализации.</summary>
public sealed class NullProcessRunner : IPlatformProcessRunner
{
    public Process StartApplication(string executablePath, string workingDirectory) =>
        throw new PlatformNotSupportedException("Платформенная реализация не настроена");

    public Process StartElevated(string executablePath, string workingDirectory) =>
        throw new PlatformNotSupportedException("Платформенная реализация не настроена");

    public Process StartManaged(string executablePath, string workingDirectory) =>
        throw new PlatformNotSupportedException("Платформенная реализация не настроена");
}
