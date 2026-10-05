using System.Net;
using System.Net.Http.Headers;
using Mfr.Core.Exceptions;

namespace Mfr.Core.Network;

/// <summary>
/// Streaming HTTP downloader with resumable ranges (Kotlin: KtorProvider
/// .downloadToFile). A partial download lives next to the target with a .part
/// suffix and is moved into place atomically, so the target either does not
/// exist or contains exactly what the server returned. Storage objects are
/// immutable once published, so stitching attempts together is safe.
/// </summary>
public sealed class HttpFileDownloader
{
    private const string PartSuffix = ".part";
    private const int BufferSize = 256 * 1024;

    private readonly HttpClient _client;
    private readonly TimeSpan _stallTimeout;

    /// <summary>Raised from worker threads; subscribers must marshal to their own context.</summary>
    public event Action<long>? BytesReceived;

    public HttpFileDownloader(HttpClient client, long? speedLimitBytesPerSecond = null, TimeSpan? stallTimeout = null)
    {
        _client = client;
        _speedLimitBytesPerSecond = speedLimitBytesPerSecond;
        _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(60);
    }

    private long? _speedLimitBytesPerSecond;

    public void SetSpeedLimit(long? bytesPerSecond) => _speedLimitBytesPerSecond = bytesPerSecond;

    /// <summary>Downloads a storage object, resuming from any previous attempt.</summary>
    public async Task DownloadToFileAsync(string host, string path, string targetPath, CancellationToken ct = default)
    {
        var partPath = targetPath + PartSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{host.TrimEnd('/')}/{path}");
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // either the object is empty or our part already covers it fully
            if (!File.Exists(partPath))
            {
                File.Create(partPath).Dispose();
            }
        }
        else if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            await CopyWithWatchdogAsync(response, partPath, append: true, ct).ConfigureAwait(false);
        }
        else if (response.IsSuccessStatusCode)
        {
            await CopyWithWatchdogAsync(response, partPath, append: false, ct).ConfigureAwait(false);
        }
        else
        {
            throw new DownloadFileException($"Загрузка {path} вернула код {(int)response.StatusCode}");
        }

        if (!File.Exists(partPath))
        {
            File.Create(partPath).Dispose();
        }
        File.Move(partPath, targetPath, overwrite: true);
    }

    /// <summary>Manifest bytes: gzip copy when offered, raw as the fallback (Kotlin: readManifest).</summary>
    public async Task<byte[]> DownloadManifestAsync(string host, string path, string? compressedPath, CancellationToken ct = default)
    {
        if (compressedPath is not null)
        {
            try
            {
                var bytes = await DownloadBytesAsync($"{host}/{compressedPath}", ct).ConfigureAwait(false);
                using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
                using var output = new MemoryStream();
                await gzip.CopyToAsync(output, ct).ConfigureAwait(false);
                return output.ToArray();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // a broken compressed copy only costs traffic, fall through to raw
            }
        }
        return await DownloadBytesAsync($"{host}/{path}", ct).ConfigureAwait(false);
    }

    private async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new DownloadFileException($"Загрузка {url} вернула код {(int)response.StatusCode}");
        }
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task CopyWithWatchdogAsync(HttpResponseMessage response, string partPath, bool append, CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var target = new FileStream(
            partPath, append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        var buffer = new byte[BufferSize];
        long windowBytes = 0;
        long windowStart = System.Environment.TickCount64;
        while (true)
        {
            var readTask = source.ReadAsync(buffer, ct).AsTask();
            var finished = await Task.WhenAny(readTask, Task.Delay(_stallTimeout, ct)).ConfigureAwait(false);
            if (finished != readTask)
            {
                throw new DownloadStalledException($"Данные не поступали {_stallTimeout.TotalSeconds:N0} секунд");
            }
            var read = await readTask.ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            BytesReceived?.Invoke(read);

            var limit = _speedLimitBytesPerSecond;
            if (limit is > 0)
            {
                windowBytes += read;
                var now = System.Environment.TickCount64;
                var elapsed = now - windowStart;
                var allowed = limit.Value * elapsed / 1000;
                if (windowBytes > allowed)
                {
                    var delay = (int)Math.Min(windowBytes * 1000 / limit.Value - elapsed, 1000);
                    if (delay > 0)
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                    windowBytes = 0;
                    windowStart = System.Environment.TickCount64;
                }
            }
        }
    }
}

/// <summary>No data received within the stall timeout (Kotlin: DownloadStalledException).</summary>
public sealed class DownloadStalledException : Exception
{
    public DownloadStalledException(string message) : base(message)
    {
    }
}
