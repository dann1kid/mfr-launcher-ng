namespace Mfr.Core.Network;

/// <summary>
/// Журнал загрузок рядом с exe (downloads.log, ротация 1 МБ): пофайловые старты,
/// размеры, сегменты, повторы и итоговая скорость. task-trace.log принадлежит
/// окну, загрузки пишут своё — поддержка читает оба.
/// </summary>
public static class DownloadLog
{
    private const long RotationBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static void Append(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "downloads.log");
                var rotated = path + ".old";
                if (File.Exists(path) && new FileInfo(path).Length > RotationBytes)
                {
                    File.Delete(rotated);
                    File.Move(path, rotated);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\n");
            }
        }
        catch (IOException)
        {
            // журнал не должен ломать загрузку
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
