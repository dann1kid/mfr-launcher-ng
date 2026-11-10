using Mfr.Core.Platform;

namespace Mfr.Platform.Windows;

/// <summary>
/// Named Mutex + EventWaitHandle — только на Windows.
/// Мьютекс кроссплатформенный в .NET, EventWaitHandle с именем — нет.
/// </summary>
public sealed class WindowsSingleInstance : IPlatformSingleInstance
{
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private readonly string _mutexName;
    private readonly string _signalName;

    public WindowsSingleInstance(string folderTag)
    {
        _mutexName = $"MFR-Launcher-{folderTag}";
        _signalName = $"MFR-Launcher-Show-{folderTag}";
    }

    public bool Acquire()
    {
        _mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
        if (!createdNew)
        {
            SignalShowExisting();
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, _signalName);
        return true;
    }

    public void SignalShowExisting()
    {
        try
        {
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, _signalName);
            signal.Set();
        }
        catch (Exception)
        {
            // первый экземпляр мог закрыться
        }
    }

    /// <summary>Ждать сигнал «показаться» (блокирующий вызов в фоновом потоке).</summary>
    public bool WaitForShow(int timeoutMs)
    {
        return _showSignal?.WaitOne(timeoutMs) ?? false;
    }

    public void Dispose()
    {
        _showSignal?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch (Exception) { }
        _mutex?.Dispose();
    }
}
