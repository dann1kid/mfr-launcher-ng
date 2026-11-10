using System.Security.Cryptography;
using Avalonia;

namespace Mfr.Launcher;

internal static class Program
{
    /// <summary>БТ-11.2: из одной папки — один экземпляр. Второй запуск показывает окно первого и завершается.</summary>
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _showSignal;

    [STAThread]
    public static void Main(string[] args)
    {
        System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {e.ExceptionObject}\n");
        if (!AcquireSingleInstance())
        {
            return; // первый экземпляр уже работает — попросили его показаться и выходим
        }
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] FATAL {exception}\n");
            throw;
        }
        finally
        {
            _showSignal?.Dispose();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
    }

    private static bool AcquireSingleInstance()
    {
        var folderTag = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory)));
        _instanceMutex = new Mutex(initiallyOwned: true, $"MFR-Launcher-{folderTag}", out var createdNew);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $"MFR-Launcher-Show-{folderTag}");
        if (createdNew)
        {
            // первый экземпляр: слушаем просьбу «показаться» от вторых запусков
            var listener = new Thread(WaitForShowSignal) { IsBackground = true };
            listener.Start();
            return true;
        }
        _showSignal.Set();
        _showSignal.Dispose();
        _instanceMutex.Dispose();
        _instanceMutex = null;
        _showSignal = null;
        return false;
    }

    private static void WaitForShowSignal()
    {
        while (_showSignal is { } signal)
        {
            try
            {
                if (!signal.WaitOne(TimeSpan.FromSeconds(30)))
                {
                    continue;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime
                        is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
                {
                    window.Show();
                    window.WindowState = Avalonia.Controls.WindowState.Normal;
                    window.Activate();
                }
            });
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
            })
            .With(new X11PlatformOptions
            {
                RenderingMode = [X11RenderingMode.Glx, X11RenderingMode.Software],
            })
            .LogToTrace();
}
