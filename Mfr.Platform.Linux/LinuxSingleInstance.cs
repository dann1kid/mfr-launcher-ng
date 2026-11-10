using System.Threading;
using Mfr.Core.Platform;

namespace Mfr.Platform.Linux;

/// <summary>
/// Named Mutex работает кроссплатформенно в .NET (через shared memory).
/// Сигнал «показаться» не поддерживается на Linux — второй запуск молча выходит.
/// </summary>
public sealed class LinuxSingleInstance : IPlatformSingleInstance
{
    private Mutex? _mutex;

    public LinuxSingleInstance(string folderTag)
    {
    }

    public bool Acquire()
    {
        _mutex = new Mutex(initiallyOwned: true, $"MFR-Launcher-Single", out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        return true;
    }

    public void SignalShowExisting()
    {
        // на Linux нет named EventWaitHandle — просто выходим
    }

    public void Dispose()
    {
        try { _mutex?.ReleaseMutex(); } catch (Exception) { }
        _mutex?.Dispose();
    }
}
