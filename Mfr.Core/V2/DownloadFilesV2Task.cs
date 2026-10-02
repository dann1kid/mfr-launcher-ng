using System.Collections.Concurrent;
using Mfr.Core.Exceptions;
using Mfr.Core.Network;

using Mfr.Core.Storage;
using Mfr.Core.Tasks;

namespace Mfr.Core.V2;

/// <summary>A file of the v2 download plan (Kotlin: DownloadFileTask.Properties.File).</summary>
public sealed record V2FilePlan(
    string MainPath,
    string? OptionalPath,
    string Storage,
    string? CompressedStorage,
    byte[] Sha256);

/// <summary>Parameters of the v2 download task.</summary>
public sealed record V2DownloadParameters(string Host, IReadOnlyList<V2FilePlan> Files, bool ApplyOptionalPath);

/// <summary>
/// v2 file download task (Kotlin: DownloadFileTask): SHA-256 analysis of the
/// existing files (10 parallel), then connectionCount workers pull from a
/// shared queue; each file goes through the gzip copy when offered and falls
/// back to the raw object on any problem; drops and stalls resume from the
/// .part offset, checksum mismatch restarts the file from scratch.
/// </summary>
public sealed class DownloadFilesV2Task(LauncherServicesV2 services) : LauncherTask<V2DownloadParameters, object?>(services.Core)
{
    private const int MaxAttempts = 5;
    private const string PackedSuffix = ".packed";
    private const string UnpackedSuffix = ".unpacked";

    protected override async Task<object?> Action(V2DownloadParameters parameters, CancellationToken cancellationToken)
    {
        Describe("Подготовка к скачиванию");
        var total = parameters.Files.Count;
        var downloaded = 0;

        Describe("Анализ существующих файлов");
        Report(0, total);
        var queue = new ConcurrentQueue<V2FilePlan>();
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
                        Report(current, total);
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
                        Describe($"Скачано файлов: {current}/{total}");
                        Report(current, total);
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
        await Task.WhenAll(workers).ConfigureAwait(false);

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
    /// copy only saves traffic: any problem with it means the raw object.
    /// </summary>
    private async Task DownloadWithRetriesAsync(string host, V2FilePlan file, CancellationToken cancellationToken)
    {
        var mainPath = Services.Paths.Resolve(file.MainPath);
        var attempt = 1;
        var useCompressed = file.CompressedStorage is not null;
        while (true)
        {
            try
            {
                if (useCompressed)
                {
                    var unpackedOk = false;
                    try
                    {
                        unpackedOk = await DownloadCompressedAsync(host, file.CompressedStorage!, mainPath, cancellationToken).ConfigureAwait(false)
                            && Sha256.Matches(mainPath, file.Sha256);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        unpackedOk = false;
                    }
                    if (unpackedOk)
                    {
                        return;
                    }
                    TryDelete(mainPath);
                    useCompressed = false;
                    continue;
                }

                await services.Downloader.DownloadToFileAsync(host, file.Storage, mainPath, cancellationToken).ConfigureAwait(false);
                if (!Sha256.Matches(mainPath, file.Sha256))
                {
                    TryDelete(mainPath);
                    throw new InvalidOperationException(
                        $"Download failed. {file.MainPath} has incorrect checksum");
                }
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DownloadFileException)
            {
                throw; // server-side failures were already retried by the HTTP client
            }
            catch (Exception exception) when (attempt < MaxAttempts)
            {
                attempt++;
                await Task.Delay(200 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Downloads the gzip copy next to the target and unpacks it into place.</summary>
    private async Task<bool> DownloadCompressedAsync(string host, string storage, string target, CancellationToken cancellationToken)
    {
        var packed = target + PackedSuffix;
        var unpacked = target + UnpackedSuffix;
        try
        {
            await services.Downloader.DownloadToFileAsync(host, storage, packed, cancellationToken).ConfigureAwait(false);
            await using var input = new System.IO.Compression.GZipStream(File.OpenRead(packed), System.IO.Compression.CompressionMode.Decompress);
            await using var output = File.Create(unpacked);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.DisposeAsync().ConfigureAwait(false);
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
