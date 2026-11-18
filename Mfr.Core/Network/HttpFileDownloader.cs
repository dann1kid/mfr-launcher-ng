using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Mfr.Core.Exceptions;

namespace Mfr.Core.Network;

/// <summary>
/// Streaming HTTP downloader with resumable ranges. A partial download lives next to the target with a .part
/// suffix and is moved into place atomically, so the target either does not
/// exist or contains exactly what the server returned. Storage objects are
/// immutable once published, so stitching attempts together is safe.
/// БТ-1.6: крупный объект качается несколькими диапазонами параллельно; прогресс
/// диапазонов — в sidecar-файле .part.segs, пауза и обрыв продолжают каждый
/// диапазон со своего места. Общий бюджет соединений делится между файлами.
/// </summary>
public sealed class HttpFileDownloader
{
    private const string PartSuffix = ".part";
    private const string SegmentsSuffix = ".segs";
    private const int BufferSize = 256 * 1024;

    /// <summary>БТ-1.6: объект меньше порога не делится — мелочь множеством
    /// соединений не ускорить, а плодить диапазонные запросы не нужно.</summary>
    internal const long SegmentThresholdBytes = 32L * 1024 * 1024;

    /// <summary>Минимальный размер диапазона: делить мельче нет смысла.</summary>
    private const long MinSegmentBytes = 8L * 1024 * 1024;

    private readonly HttpClient _client;
    private readonly TimeSpan _stallTimeout;
    private readonly SemaphoreSlim _streamSlots;

    /// <summary>Raised from worker threads; subscribers must marshal to their own context.</summary>
    public event Action<long>? BytesReceived;

    public HttpFileDownloader(HttpClient client, long? speedLimitBytesPerSecond = null, TimeSpan? stallTimeout = null,
        int maxParallelStreams = 20)
    {
        _client = client;
        _speedLimitBytesPerSecond = speedLimitBytesPerSecond;
        _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(60);
        _streamSlots = new SemaphoreSlim(Math.Max(1, maxParallelStreams));
    }

    private long? _speedLimitBytesPerSecond;

    public void SetSpeedLimit(long? bytesPerSecond)
    {
        lock (_throttleLock)
        {
            _speedLimitBytesPerSecond = bytesPerSecond;
            _tokens = bytesPerSecond ?? 0; // старт с полным ведром
            _refillTimestamp = System.Environment.TickCount64;
        }
    }

    /// <summary>Downloads a storage object, resuming from any previous attempt.
    /// БТ-1.1: с ожидаемой суммой файл сверяется до переноса на окончательное имя.
    /// segmentHint &gt; 1 разрешает диапазонную загрузку крупного объекта (БТ-1.6).</summary>
    public async Task DownloadToFileAsync(string host, string path, string targetPath, CancellationToken ct = default,
        byte[]? expectedSha256 = null, int segmentHint = 1)
    {
        var url = $"{host.TrimEnd('/')}/{path}";
        var partPath = targetPath + PartSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        if (File.Exists(partPath + SegmentsSuffix))
        {
            // продолжение нарезанной загрузки: sidecar есть — диапазоны продолжаются,
            // даже если подсказка теперь 1 (нарезка была начата, .part предвыделен целиком)
            if (await TryDownloadSegmentedAsync(url, path, partPath, ct, Math.Max(segmentHint, 2)).ConfigureAwait(false))
            {
                await VerifyAndMoveAsync(path, partPath, targetPath, expectedSha256).ConfigureAwait(false);
                return;
            }
            // сервер перестал держать диапазоны — нарезку не продолжить, заново одним потоком
            TryDeleteQuiet(partPath + SegmentsSuffix);
            TryDeleteQuiet(partPath);
        }
        else if (segmentHint > 1 && !File.Exists(partPath))
        {
            if (await TryDownloadSegmentedAsync(url, path, partPath, ct, segmentHint).ConfigureAwait(false))
            {
                await VerifyAndMoveAsync(path, partPath, targetPath, expectedSha256).ConfigureAwait(false);
                return;
            }
            // сервер без диапазонов или объект мелкий — обычный однопоточный путь
        }
        // легаси .part без sidecar доскачивается одним потоком: смешивать форматы нельзя
        await DownloadSingleStreamAsync(url, path, partPath, ct, expectedSha256).ConfigureAwait(false);
        await VerifyAndMoveAsync(path, partPath, targetPath, expectedSha256).ConfigureAwait(false);
    }

    private async Task DownloadSingleStreamAsync(string url, string path, string partPath, CancellationToken ct,
        byte[]? expectedSha256)
    {
        await _streamSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
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
                // БТ-1/прогресс: докачиваемая часть уже на диске — считаем её готовой
                BytesReceived?.Invoke(offset);
                await using var target = OpenAppend(partPath, append: true);
                await CopyWithWatchdogAsync(response, target, ct).ConfigureAwait(false);
            }
            else if (response.IsSuccessStatusCode)
            {
                await using var target = OpenAppend(partPath, append: false);
                await CopyWithWatchdogAsync(response, target, ct).ConfigureAwait(false);
            }
            else
            {
                throw new DownloadFileException($"Загрузка {path} вернула код {(int)response.StatusCode}",
                    statusCode: (int)response.StatusCode);
            }
            if (!File.Exists(partPath))
            {
                File.Create(partPath).Dispose();
            }
        }
        finally
        {
            _streamSlots.Release();
        }
    }

    private FileStream OpenAppend(string partPath, bool append) => new(
        partPath, append ? FileMode.Append : FileMode.Create,
        FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

    /// <summary>
    /// БТ-1.6: диапазонная загрузка. Возвращает false, если сервер не держит
    /// диапазоны или объект оказался меньше порога — звавший уйдёт в однопоточную.
    /// </summary>
    private async Task<bool> TryDownloadSegmentedAsync(string url, string path, string partPath, CancellationToken ct,
        int segmentHint)
    {
        // проба диапазоном 0-0: даёт общий размер объекта из Content-Range
        long total;
        await _streamSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(0, 0) } };
            using var response = await _client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Create(partPath).Dispose(); // пустой объект
                return true;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new DownloadFileException($"Загрузка {path} вернула код {(int)response.StatusCode}",
                    statusCode: (int)response.StatusCode);
            }
            if (response.StatusCode != HttpStatusCode.PartialContent
                || response.Content.Headers.ContentRange?.HasRange is not true
                || response.Content.Headers.ContentRange.Length is not { } length)
            {
                return false; // сервер без диапазонов
            }
            total = length;
        }
        finally
        {
            _streamSlots.Release();
        }
        if (total < SegmentThresholdBytes)
        {
            return false; // мелкий объект — одним потоком
        }

        // БТ-1.6/возобновление: продолжаем НАРЕЗКОЙ ИЗ SIDECAR — смена подсказки
        // (настройки соединений) между паузой и повтором не перекачивает объект
        var done = LoadSegments(partPath + SegmentsSuffix, total);
        if (done is not null
            && (!File.Exists(partPath) || new FileInfo(partPath).Length != total))
        {
            // .part удалён или повреждён, sidecar осиротел — самооздоровление: заново
            TryDeleteQuiet(partPath);
            TryDeleteQuiet(partPath + SegmentsSuffix);
            done = null;
        }
        var segments = done?.Length
            ?? (int)Math.Clamp(segmentHint, 1, (long)Math.Ceiling(total / (double)MinSegmentBytes));
        if (done is null)
        {
            TryDeleteQuiet(partPath);
            using (var fresh = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                fresh.SetLength(total); // место под все диапазоны сразу, сборка не нужна
            }
            done = new long[segments];
        }
        var bounds = new long[segments + 1];
        for (var i = 0; i <= segments; i++)
        {
            bounds[i] = total * i / segments;
        }
        var resumed = 0L;
        for (var i = 0; i < segments; i++)
        {
            resumed += done[i];
        }
        if (resumed > 0)
        {
            BytesReceived?.Invoke(resumed); // БТ-1/прогресс: докачиваемое уже на диске
        }
        DownloadLog.Append($"segments: {path} → {Mfr.Core.V2.FormatBytes2.Format(total)}, {segments} диапазонов, с места {Mfr.Core.V2.FormatBytes2.Format(resumed)}");

        var failures = new ConcurrentQueue<Exception>();
        var segmentTasks = Enumerable.Range(0, segments)
            .Where(i => done[i] < bounds[i + 1] - bounds[i])
            .Select(i => DownloadSegmentAsync(url, partPath, done, bounds, i, failures, ct))
            .ToArray();
        try
        {
            await Task.WhenAll(segmentTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SaveSegments(partPath + SegmentsSuffix, total, done); // пауза: каждый диапазон продолжится со своего места
            throw;
        }
        if (!failures.IsEmpty)
        {
            SaveSegments(partPath + SegmentsSuffix, total, done); // обрыв: прогресс диапазонов сохранён
            throw failures.First();
        }
        TryDeleteQuiet(partPath + SegmentsSuffix);
        return true;
    }

    /// <summary>Один диапазон: с места done[i] до конца диапазона; прогресс сразу в файл и sidecar.</summary>
    private async Task DownloadSegmentAsync(string url, string partPath, long[] done, long[] bounds, int index,
        ConcurrentQueue<Exception> failures, CancellationToken ct)
    {
        try
        {
            await _streamSlots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var start = bounds[index] + done[index];
                var end = bounds[index + 1] - 1;
                using var request = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Headers = { Range = new RangeHeaderValue(start, end) },
                };
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    // 4xx и прочее без повтора — как у однопоточного пути; 5xx уйдёт в retry
                    throw new DownloadFileException(
                        $"Диапазон {index + 1} вернул код {(int)response.StatusCode}",
                        statusCode: (int)response.StatusCode);
                }
                var actualFrom = response.Content.Headers.ContentRange?.From;
                if (actualFrom != start)
                {
                    // RFC 9110: чужой Content-Range затёр бы соседний диапазон — не пишем
                    throw new IOException($"Сервер вернул диапазон с {actualFrom}, ожидался с {start}");
                }
                await using var target = new FileStream(partPath, FileMode.Open, FileAccess.Write,
                    FileShare.Read | FileShare.Write, BufferSize, useAsync: true)
                {
                    Position = start,
                };
                await CopyWithWatchdogAsync(response, target, ct, read => done[index] += read).ConfigureAwait(false);
            }
            finally
            {
                _streamSlots.Release();
            }
            SaveSegments(partPath + SegmentsSuffix, bounds[bounds.Length - 1], done, index);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Enqueue(exception); // остальные диапазоны доделывают своё, ошибка взлетит одна
        }
    }

    /// <summary>Прогресс диапазонов: первая строка — общий размер, дальше — сделанное.
    /// Число строк задаёт прежняя нарезка — подсказка на возобновлении не важна.</summary>
    private static long[]? LoadSegments(string segPath, long total)
    {
        try
        {
            if (!File.Exists(segPath))
            {
                return null;
            }
            var lines = File.ReadAllLines(segPath);
            if (lines.Length < 2 || !long.TryParse(lines[0], out var savedTotal) || savedTotal != total)
            {
                return null;
            }
            var done = new long[lines.Length - 1];
            for (var i = 0; i < done.Length; i++)
            {
                if (!long.TryParse(lines[i + 1], out done[i]) || done[i] < 0)
                {
                    return null;
                }
            }
            return done;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>tmp уникален по сегменту: одновременные сохранения не спорят за файл.</summary>
    private static void SaveSegments(string segPath, long total, long[] done, int? writer = null)
    {
        try
        {
            var lines = new string[done.Length + 1];
            lines[0] = total.ToString();
            for (var i = 0; i < done.Length; i++)
            {
                lines[i + 1] = done[i].ToString();
            }
            var tmp = segPath + ".tmp" + writer?.ToString();
            File.WriteAllLines(tmp, lines);
            File.Move(tmp, segPath, overwrite: true);
        }
        catch (IOException)
        {
            // sidecar не записался — следующий старт чуть перекачает, сумма всё равно охраняет
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task VerifyAndMoveAsync(string path, string partPath, string targetPath,
        byte[]? expectedSha256)
    {
        if (expectedSha256 is { Length: > 0 } expected)
        {
            string actual;
            using (var stream = File.OpenRead(partPath))
            {
                actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            }
            if (!string.Equals(actual, Convert.ToHexString(expected), StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteQuiet(partPath);
                TryDeleteQuiet(partPath + SegmentsSuffix);
                throw new DownloadFileException($"Скачанный файл {path} повреждён (сумма не совпала)");
            }
        }
        File.Move(partPath, targetPath, overwrite: true);
    }

    // общий бюджет скорости на весь загрузчик: сумма по всем соединениям (БТ-11)
    private readonly object _throttleLock = new();
    private long _tokens;
    private long _refillTimestamp = System.Environment.TickCount64;

    private async Task ThrottleAsync(int read, CancellationToken ct)
    {
        var limit = _speedLimitBytesPerSecond;
        if (limit is not > 0)
        {
            return;
        }
        // токен-ведро ёмкостью в одну секунду лимита: пополнение один раз по
        // прошедшему времени (сколько бы воркеров ни входило), вычитаются только
        // свои байты — средняя скорость держится точно при любом числе потоков;
        // простой наполняет не больше ёмкости
        while (true)
        {
            long debt;
            lock (_throttleLock)
            {
                var now = System.Environment.TickCount64;
                var refill = Math.Min(now - _refillTimestamp, 1000) * limit.Value / 1000;
                _refillTimestamp = now;
                _tokens = Math.Min(_tokens + refill, limit.Value);
                _tokens -= read;
                read = 0;
                debt = -Math.Min(_tokens, 0);
            }
            if (debt == 0)
            {
                return;
            }
            var slice = (int)Math.Min(debt * 1000 / limit.Value, 200);
            await Task.Delay(slice, ct).ConfigureAwait(false);
        }
    }

    private static void TryDeleteQuiet(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Manifest bytes: gzip copy when offered, raw as the fallback.</summary>
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

    private const int MaxRetries = 5;

    /// <summary>Ошибка связи: повторяется с растущей паузой (БТ-10.1) — сеть,
    /// 5xx хранилища, зависание и таймаут. Таймаут отличён от отмены по токену.</summary>
    private static bool IsRetryable(Exception exception) =>
        exception is HttpRequestException or IOException or DownloadStalledException ||
        (exception is OperationCanceledException canceled && canceled.InnerException is TimeoutException) ||
        (exception is DownloadFileException download && download.StatusCode is >= 500 and <= 599);

    /// <summary>Манифесты: ответы 5xx, сетевые ошибки и таймауты повторяются
    /// с растущей паузой до сырой попытки (БТ-10.1).</summary>
    private async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new DownloadFileException($"Загрузка {url} вернула код {(int)response.StatusCode}",
                        statusCode: (int)response.StatusCode);
                }
                return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryable(exception) && attempt < MaxRetries)
            {
                await Task.Delay(Backoff.Delay(attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task CopyWithWatchdogAsync(HttpResponseMessage response, Stream target, CancellationToken ct,
        Action<int>? onRead = null)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[BufferSize];
        while (true)
        {
            var readTask = source.ReadAsync(buffer, ct).AsTask();
            var finished = await Task.WhenAny(readTask, Task.Delay(_stallTimeout, ct)).ConfigureAwait(false);
            if (finished != readTask)
            {
                ct.ThrowIfCancellationRequested(); // пауза игрока, а не зависание
                throw new DownloadStalledException($"Данные не поступали {_stallTimeout.TotalSeconds:N0} секунд");
            }
            var read = await readTask.ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            BytesReceived?.Invoke(read);
            onRead?.Invoke(read);

            await ThrottleAsync(read, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>No data received within the stall timeout.</summary>
public sealed class DownloadStalledException : Exception
{
    public DownloadStalledException(string message) : base(message)
    {
    }
}
