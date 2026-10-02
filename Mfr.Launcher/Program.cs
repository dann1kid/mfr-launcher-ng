using Avalonia;

namespace Mfr.Launcher;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            System.IO.File.AppendAllText("crash.log", $"[{DateTime.Now:HH:mm:ss.fff}] {e.ExceptionObject}\n");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            System.IO.File.AppendAllText("crash.log", $"[{DateTime.Now:HH:mm:ss.fff}] FATAL {exception}\n");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
            })
            .LogToTrace();
}
