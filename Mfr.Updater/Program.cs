using System.Diagnostics;

// Launcher self-update helper (Kotlin: PluginConfigurationUtilityUpdater, but
// waits for the real process exit instead of a blind 5-second delay).
// Usage: Mfr.Updater.exe "file_name=<downloaded exe>" "pid=<launcher pid>"

var source = FindArg("file_name");
var pidText = FindArg("pid");
if (source is null || !File.Exists(source))
{
    Console.WriteLine("usage: Mfr.Updater.exe \"file_name=<path>\" \"pid=<launcher pid>\"");
    return 1;
}

var target = Path.Combine(AppContext.BaseDirectory, "Mfr.Launcher.exe");

// wait up to 60 seconds for the launcher process to exit
if (int.TryParse(pidText, out var pid))
{
    for (var i = 0; i < 120; i++)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            break; // already gone
        }
        process.Dispose();
        Thread.Sleep(500);
    }
}

// replace the executable, retrying while the file is still locked
for (var attempt = 0; ; attempt++)
{
    try
    {
        using (var input = File.OpenRead(source))
        using (var output = File.Create(target))
        {
            input.CopyTo(output);
        }
        File.Delete(source);
        break;
    }
    catch (IOException) when (attempt < 20)
    {
        Thread.Sleep(500);
    }
}

Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
return 0;

static string? FindArg(string key)
{
    foreach (var arg in Environment.GetCommandLineArgs())
    {
        if (arg.StartsWith(key + "=", StringComparison.Ordinal))
        {
            return arg[(key.Length + 1)..].Trim('"');
        }
    }
    return null;
}
