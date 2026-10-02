using Mfr.Core.Network;
using Mfr.Protocol.Cryptography;
using Mfr.Protocol.Dto;

namespace Mfr.Core.Tasks;

/// <summary>
/// Downloads manifest files, skipping the ones already on disk with a
/// matching MD5 (Kotlin: DownloadGameFileTask). Reports progress in percent of
/// total bytes and download speed in the description.
/// </summary>
public sealed class DownloadFilesTask(LauncherServices services) : LauncherTask<IReadOnlyList<FileDto>, object?>(services)
{
    protected override async Task<object?> Action(IReadOnlyList<FileDto> files, CancellationToken cancellationToken)
    {
        Describe("Подготовка к скачиванию");
        var totalSize = files.Sum(f => f.Size);
        long downloaded = 0;

        Describe("Анализ существующих файлов");
        var toDownload = new List<FileRequest>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = Services.Paths.Resolve(file.Path);
            if (File.Exists(targetPath))
            {
                using var stream = File.OpenRead(targetPath);
                if (Md5.Hash(stream).AsSpan().SequenceEqual(file.Md5))
                {
                    downloaded += file.Size;
                    Report(downloaded, totalSize);
                    continue;
                }
            }
            toDownload.Add(new FileRequest(file.Id, targetPath, file.Size, file.Md5));
        }

        if (toDownload.Count == 0)
        {
            Report(100);
            return null;
        }

        long windowBytes = 0;
        void OnBytesReceived(long count)
        {
            downloaded += count;
            Interlocked.Add(ref windowBytes, count);
            Report(downloaded, totalSize);
        }

        Services.Downloader.BytesReceived += OnBytesReceived;
        using var speedLoop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var speedReporter = Task.Run(async () =>
        {
            while (!speedLoop.IsCancellationRequested)
            {
                await Task.Delay(1000, speedLoop.Token).ConfigureAwait(false);
                Describe($"Скорость скачивания: {FormatBytes(Interlocked.Exchange(ref windowBytes, 0))}/с");
            }
        }, CancellationToken.None);
        try
        {
            await Services.Downloader.DownloadGameFiles(toDownload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Services.Downloader.BytesReceived -= OnBytesReceived;
            speedLoop.Cancel();
            try
            {
                await speedReporter.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        return null;
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} МБ",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} ГБ",
    };
}
