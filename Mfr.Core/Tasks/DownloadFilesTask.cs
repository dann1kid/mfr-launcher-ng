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

        void OnBytesReceived(long count)
        {
            downloaded += count;
            Report(downloaded, totalSize);
        }

        // the old client's live counter (5.0.15 DownloadFileTask): one line, no speed
        // (its speed formatter is dead code there)
        var filesCompleted = 0;
        void OnFileCompleted(FileRequest _, bool __) =>
            Describe($"Скачано файлов: {Interlocked.Increment(ref filesCompleted)}");

        Services.Downloader.BytesReceived += OnBytesReceived;
        Services.Downloader.FileCompleted += OnFileCompleted;
        try
        {
            await Services.Downloader.DownloadGameFiles(toDownload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Services.Downloader.BytesReceived -= OnBytesReceived;
            Services.Downloader.FileCompleted -= OnFileCompleted;
        }
        return null;
    }
}
