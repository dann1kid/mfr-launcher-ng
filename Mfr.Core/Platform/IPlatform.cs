using System.Diagnostics;

namespace Mfr.Core.Platform;

/// <summary>
/// Платформенный запуск процессов. Реализации — в Mfr.Platform.&lt;ОС&gt;.
/// Ядро не ветвится по ОС (правило AGENTS.md).
/// </summary>
public interface IPlatformProcessRunner
{
    /// <summary>Запустить игру/утилиту в рабочем каталоге.</summary>
    Process StartApplication(string executablePath, string workingDirectory);

    /// <summary>Запустить с повышением прав (UAC / pkexec / sudo).</summary>
    Process StartElevated(string executablePath, string workingDirectory);

    /// <summary>Запустить и вернуть процесс для отслеживания (MGE).</summary>
    Process StartManaged(string executablePath, string workingDirectory);
}

/// <summary>
/// Единственный экземпляр клиента. На Windows — named Mutex + EventWaitHandle,
/// на Linux/macOS — POSIX file lock или именованный Mutex (кроссплатформенный в .NET).
/// </summary>
public interface IPlatformSingleInstance : IDisposable
{
    /// <summary>Первый экземпляр? false — второй запуск должен показать первый и выйти.</summary>
    bool Acquire();

    /// <summary>Сигнал первому экземпляру «покажись». Только Windows.</summary>
    void SignalShowExisting();
}

/// <summary>Замена собственного exe при самообновлении.</summary>
public interface IPlatformSelfUpdate
{
    /// <summary>Заменить exe и перезапуститься. Реализация зависит от ОС (реестр, права, путь).</summary>
    void ReplaceAndRestart(string downloadedExePath);
}
