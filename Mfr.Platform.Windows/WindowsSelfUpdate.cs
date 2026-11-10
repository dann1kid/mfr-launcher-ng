using Mfr.Core.Platform;

namespace Mfr.Platform.Windows;

/// <summary>Замена exe in-place с .old-свапом — безопасно на Windows (файл можно переименовать).</summary>
public sealed class WindowsSelfUpdate : IPlatformSelfUpdate
{
    public void ReplaceAndRestart(string downloadedExePath)
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь клиента");
        var oldPath = executablePath + ".old";
        try { File.Delete(oldPath); } catch (IOException) { }
        File.Move(executablePath, oldPath);
        try
        {
            File.Copy(downloadedExePath, executablePath);
        }
        catch (Exception)
        {
            File.Move(oldPath, executablePath, overwrite: true);
            throw;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
        });
        Environment.Exit(0);
    }
}
