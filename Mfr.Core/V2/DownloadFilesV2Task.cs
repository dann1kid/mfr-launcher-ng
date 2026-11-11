using System.Collections.Concurrent;
using Mfr.Core.Exceptions;
using Mfr.Core.Network;

using Mfr.Core.Storage;
using Mfr.Core.Tasks;

namespace Mfr.Core.V2;

/// <summary>A file of the v2 download plan.</summary>
/// <remarks>Контракт: compression отсутствует или незнакома — качается сырой объект,
/// поэтому умолчания нет и значение передаётся явно каждым вызовом.</remarks>
public sealed record V2FilePlan(
    string MainPath,
    string? OptionalPath,
    string Storage,
    string? CompressedStorage,
    byte[] Sha256,
    string Compression,
    ulong? SizeBytes = null);

/// <summary>Parameters of the v2 download task.</summary>
public sealed record V2DownloadParameters(string Host, IReadOnlyList<V2FilePlan> Files, bool ApplyOptionalPath);

/// <summary>
/// v2 file download task: SHA-256 analysis of the
/// existing files (10 parallel), then connectionCount workers pull from a
/// shared queue; each file goes through the gzip copy when offered, breaks
/// resume the copy and only an unusable copy falls back to the raw object;
/// drops and stalls resume from the .part offset, checksum mismatch restarts
/// the file from scratch.
/// </summary>
public sealed class DownloadFilesV2Task(LauncherServicesV2 services) : LauncherTask<V2DownloadParameters, object?>(services.Core)
{
    private const int MaxAttempts = 5;
    private const string PackedSuffix = ".packed";
    private const string PartSuffix = ".part";
    private const string UnpackedSuffix = ".unpacked";

    protected override async Task<object?> Action(V2DownloadParameters parameters, CancellationToken cancellationToken)
    {
        Describe("Подготовка к скачиванию");
        var total = parameters.Files.Count;
        // БТ-1: прогресс в байтах, когда схема несёт размеры; иначе счётчик файлов
        var totalBytes = parameters.Files.Sum(f => (long?)f.SizeBytes);
        var bytesKnown = totalBytes is not null && totalBytes > 0 && totalBytes < 2_000_000_000_000 && parameters.Files.All(f => f.SizeBytes is not null);
        // totalBytes > 2 ТБ для игры — манифест с раздутыми размерами, не верим байтам
        var downloaded = 0;

        Describe("Анализ существующих файлов");
        if (bytesKnown)
        {
            Report(0, totalBytes!.Value);
        }
        else
        {
            Report(0, total);
        }
        var queue = new ConcurrentQueue<V2FilePlan>();
        long bytesDownloaded = 0;
        void OnBytes(long count) => Interlocked.Add(ref bytesDownloaded, count);
        using (var analysisLimiter = new SemaphoreSlim(10))
        {
            var checks = parameters.Files.Select(async file =>
            {
                await analysisLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var mainPath = Services.Paths.Resolve(file.MainPath);
                    if (File.Exists(mainPath) && Sha256.Matches(mainPath, file.Sha256))
                    {
                        if (parameters.ApplyOptionalPath && file.OptionalPath is { } optionalPath)
                        {
                            CopyTo(mainPath, Services.Paths.Resolve(optionalPath));
                        }
                        var current = Interlocked.Increment(ref downloaded);
                        // БТ-1/прогресс: готовый файл сразу в числителе байтов —
                        // после продолжения полоса не начинается с нуля
                        if (file.SizeBytes is { } size)
                        {
                            OnBytes((long)size);
                            if (bytesKnown)
                            {
                                Report(Volatile.Read(ref bytesDownloaded), totalBytes!.Value);
                            }
                        }
                        if (!bytesKnown)
                        {
                            Report(current, total);
                        }
                    }
                    else
                    {
                        queue.Enqueue(file);
                    }
                }
                finally
                {
                    analysisLimiter.Release();
                }
            });
            await Task.WhenAll(checks).ConfigureAwait(false);
        }

        services.Downloader.BytesReceived += OnBytes;
        var failed = new ConcurrentBag<V2FilePlan>();
        var workers = Enumerable.Range(0, Math.Clamp(services.Core.Options.Server.ConnectionCount, 1, 20))
            .Select(_ => Task.Run(async () =>
            {
                while (queue.TryDequeue(out var file))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await DownloadWithRetriesAsync(parameters.Host, file, cancellationToken).ConfigureAwait(false);
                        if (parameters.ApplyOptionalPath && file.OptionalPath is { } optionalPath)
                        {
                            CopyTo(Services.Paths.Resolve(file.MainPath), Services.Paths.Resolve(optionalPath));
                        }
                        var current = Interlocked.Increment(ref downloaded);
                        Describe($"Готово: {current}/{total} ({FormatBytes2.Format(Math.Min(Volatile.Read(ref bytesDownloaded), bytesKnown ? totalBytes!.Value : Volatile.Read(ref bytesDownloaded)))} из {FormatBytes2.Format(bytesKnown ? totalBytes!.Value : 0)})");
                        if (bytesKnown)
                        {
                            // сжатые копии качаются меньшим объёмом чем size — клампим
                            var raw = Volatile.Read(ref bytesDownloaded);
                            Report(Math.Min(raw, totalBytes!.Value), totalBytes!.Value);
                        }
                        else
                        {
                            Report(current, total);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        failed.Add(file);
                    }
                }
            }, cancellationToken))
            .ToList();
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            services.Downloader.BytesReceived -= OnBytes;
        }

        if (!failed.IsEmpty)
        {
            throw new DownloadFileException($"Не удалось скачать файлов: {failed.Count}");
        }
        Report(100);
        return null;
    }

    /// <summary>
    /// Drops and stalls resume from where they stopped; a checksum mismatch
    /// means a broken file and the next attempt starts over. The compressed
    /// copy only saves traffic: while the network breaks it, the copy itself is
    /// resumed; only a copy that cannot be downloaded or unpacked means the raw object.
    /// </summary>
    private async Task DownloadWithRetriesAsync(string host, V2FilePlan file, CancellationToken cancellationToken)
    {
        var mainPath = Services.Paths.Resolve(file.MainPath);
        // контракт: сжатая копия — только когда кодек известен (сегодня gzip)
        if (file.CompressedStorage is not null && file.Compression == "gzip")
        {
            if (await TryDownloadCompressedAsync(host, file, mainPath, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            // БТ-10.2: сжатая копия не скачалась — исходный объект; огрызок копии больше не нужен
            TryDelete(mainPath);
            TryDelete(mainPath + PackedSuffix + PartSuffix);
        }
        await DownloadRawAsync(host, file, mainPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// БТ-1.1: обрыв, зависание и таймаут сети повторяются в саму копию — Range
    /// докачивает .packed.part с места обрыва, сырой объект с нуля не начинается.
    /// Окончательно копия бракуется, только когда она не скачалась вовсе (ответ
    /// хранилища) или не распаковалась в ожидаемый файл — тогда действует БТ-10.2.
    /// </summary>
    private async Task<bool> TryDownloadCompressedAsync(string host, V2FilePlan file, string mainPath, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await DownloadCompressedAsync(host, file.CompressedStorage!, mainPath, file.Sha256, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException canceled) when (canceled.InnerException is TimeoutException)
            {
                // БТ-10.1: таймаут — ошибка связи с повтором, а не пауза игрока;
                // Range докачивает копию с места обрыва
                if (attempt >= MaxAttempts)
                {
                    return false; // копия так и не далась — исходный объект
                }
            }
            catch (OperationCanceledException)
            {
                throw; // пауза игрока: .packed.part остаётся, докачка продолжится копией
            }
            catch (DownloadFileException download) when (download.StatusCode is >= 500 and <= 599 && attempt < MaxAttempts)
            {
                // БТ-10.1: 5xx хранилища повторяется — копия ещё может даться
            }
            catch (DownloadFileException)
            {
                return false; // копия не скачалась — БТ-10.2: исходный объект
            }
            catch (Exception)
            {
                if (attempt >= MaxAttempts)
                {
                    return false; // сеть так и не дала докачать копию — исходный объект
                }
            }
            await Task.Delay(Backoff.Delay(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Сырой объект: обрывы, зависания, таймауты и 5xx повторяются
    /// с растущей паузой и докачкой по .part (БТ-10.1).</summary>
    private async Task DownloadRawAsync(string host, V2FilePlan file, string mainPath, CancellationToken cancellationToken)
    {
        var attempt = 1;
        while (true)
        {
            try
            {
                await services.Downloader.DownloadToFileAsync(host, file.Storage, mainPath, cancellationToken, file.Sha256).ConfigureAwait(false);
                if (!Sha256.Matches(mainPath, file.Sha256))
                {
                    TryDelete(mainPath);
                    throw new InvalidOperationException(
                        $"Download failed. {file.MainPath} has incorrect checksum");
                }
                return;
            }
            catch (OperationCanceledException canceled) when (canceled.InnerException is TimeoutException)
            {
                // БТ-10.1: таймаут HTTP — ошибка связи с повтором, а не пауза игрока
                if (attempt >= MaxAttempts)
                {
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                throw; // пауза игрока: .part остаётся, докачка продолжится
            }
            catch (DownloadFileException download) when (download.StatusCode is >= 500 and <= 599 && attempt < MaxAttempts)
            {
                // БТ-10.1: 5xx хранилища повторяется с растущей паузой
            }
            catch (DownloadFileException)
            {
                throw; // 4xx и несовпавшая сумма — не сетевой сбой, повтора нет
            }
            catch (Exception) when (attempt < MaxAttempts)
            {
            }
            attempt++;
            await Task.Delay(Backoff.Delay(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Downloads the gzip copy next to the target and unpacks it into place.
    /// Сумма сверяется у распакованного результата до переноса (БТ-1.1).</summary>
    private async Task<bool> DownloadCompressedAsync(string host, string storage, string target, byte[] sha256, CancellationToken cancellationToken)
    {
        var packed = target + PackedSuffix;
        var unpacked = target + UnpackedSuffix;
        try
        {
            await services.Downloader.DownloadToFileAsync(host, storage, packed, cancellationToken).ConfigureAwait(false);
            await using var input = new System.IO.Compression.GZipStream(File.OpenRead(packed), System.IO.Compression.CompressionMode.Decompress);
            await using var output = File.Create(unpacked);
            try
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                // не gzip: копия заведомо не подойдёт, повторная перекачка — зря потраченный трафик
                return false;
            }
            await output.DisposeAsync().ConfigureAwait(false);
            if (!Sha256.Matches(unpacked, sha256))
            {
                return false;
            }
            File.Move(unpacked, target, overwrite: true);
            return true;
        }
        finally
        {
            TryDelete(packed);
            TryDelete(unpacked);
        }
    }

    private void CopyTo(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Читаемое количество байт для строки состояния: подпись соответствует ступени значения.</summary>
public static class FormatBytes2
{
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} МБ",
        < 1024L * 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} ГБ",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0 / 1024.0:F1} ТБ",
    };
}
