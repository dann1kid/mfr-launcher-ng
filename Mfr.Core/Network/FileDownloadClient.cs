using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Logging;
using Mfr.Core.Exceptions;
using Mfr.Core.Options;
using Mfr.Protocol.Netty;

namespace Mfr.Core.Network;

/// <summary>
/// Downloads game files and launcher binaries over the custom TCP protocol
/// (Kotlin: NettyProvider + InboundFileHandler). One instance handles the whole
/// download session: up to <see cref="ServerOptions.ConnectionCount"/> parallel
/// TLS connections, files balanced across them, pause/resume via protocol
/// message 7.
/// </summary>
public sealed class FileDownloadClient : IDisposable
{
    private readonly LauncherOptions _options;
    private readonly ILogger<FileDownloadClient>? _logger;
    private readonly object _sessionsLock = new();
    private readonly List<ChannelSession> _sessions = [];

    /// <summary>Raised from download worker threads; subscribers must marshal to their own context.</summary>
    public event Action<long>? BytesReceived;

    /// <summary>Raised when a file's last frame arrives; the flag is true when the MD5 digest matched.</summary>
    public event Action<FileRequest, bool>? FileCompleted;

    /// <summary>
    /// Total download speed limit in bytes per second, shared by all
    /// connections; 0 disables limiting (Kotlin: GlobalTrafficShapingHandler).
    /// </summary>
    public long SpeedLimitBytesPerSecond { get; set; }

    private long _budgetBytes;
    private long _budgetTimestamp;

    private async Task ThrottleAsync(int bytesRead, CancellationToken cancellationToken)
    {
        var limit = SpeedLimitBytesPerSecond;
        if (limit <= 0)
        {
            return;
        }
        var now = Environment.TickCount64;
        _budgetBytes = Math.Min(_budgetBytes + (now - _budgetTimestamp) * limit / 1000, limit);
        _budgetTimestamp = now;
        if (_budgetBytes >= bytesRead)
        {
            _budgetBytes -= bytesRead;
            return;
        }
        var deficit = bytesRead - _budgetBytes;
        _budgetBytes = 0;
        var delay = (int)Math.Min(deficit * 1000 / limit, 1000);
        if (delay > 0)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            _budgetTimestamp = Environment.TickCount64;
        }
    }

    public FileDownloadClient(LauncherOptions options, ILogger<FileDownloadClient>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public async Task DownloadGameFiles(IReadOnlyList<FileRequest> files, CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
            return;

        var connectionCount = Math.Min(files.Count, Math.Max(1, _options.Server.ConnectionCount));
        var buckets = SplitBySize(files, connectionCount);
        _logger?.LogInformation("Downloading {FileCount} files ({Size} bytes) over {Connections} connections",
            files.Count, files.Sum(f => f.Size), buckets.Count);

        await Task.WhenAll(buckets.Select(bucket => RunSessionAsync(bucket, launcher: false, cancellationToken)))
            .ConfigureAwait(false);
    }

    /// <summary>Downloads the launcher binary (server message 5) on a single connection.</summary>
    public Task DownloadLauncher(FileRequest file, CancellationToken cancellationToken = default) =>
        RunSessionAsync([file], launcher: true, cancellationToken);

    /// <summary>Asks the server to stop/resume sending frames on every active connection.</summary>
    public void Pause() => BroadcastState(active: false);

    public void Resume() => BroadcastState(active: true);

    private async Task RunSessionAsync(IReadOnlyList<FileRequest> files, bool launcher, CancellationToken cancellationToken)
    {
        var session = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            var request = launcher
                ? (Message)new RequestLauncherFilesMessage(_options.ClientId, _options.Platform)
                : new RequestGameFilesMessage(_options.ClientId, files.Select(f => f.Id).ToArray());
            await session.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await ReceiveLoopAsync(session, files, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ChannelSession> ConnectAsync(CancellationToken cancellationToken)
    {
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(60));

        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(_options.Server.Address, _options.Server.TcpPort, connectTimeout.Token)
                .ConfigureAwait(false);
            tcp.NoDelay = true;
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _options.Server.Address,
            }, connectTimeout.Token).ConfigureAwait(false);

            var session = new ChannelSession(ssl);
            lock (_sessionsLock)
            {
                _sessions.Add(session);
            }
            return session;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private async Task ReceiveLoopAsync(ChannelSession session, IReadOnlyList<FileRequest> files, CancellationToken cancellationToken)
    {
        var byId = files.ToDictionary(f => f.Id);
        var slots = new Dictionary<int, FileSlot>();
        try
        {
            var reader = new FrameReader();
            var buffer = new byte[64 * 1024];
            var serverFinished = false;
            var closeReason = "eof";
            while (!serverFinished)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await session.Stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break; // server closed the channel
                closeReason = "eof";
                await ThrottleAsync(read, cancellationToken).ConfigureAwait(false);
                reader.Append(buffer.AsSpan(0, read));

                while (reader.TryReadFrame() is { } payload)
                {
                    switch (MessageCodec.Decode(payload))
                    {
                        case UploadFileMessage upload:
                            WriteFrame(slots, byId, upload);
                            break;

                        case EndSessionMessage:
                            // The old client closes the channel here; the server sends nothing after it.
                            serverFinished = true;
                            closeReason = "endSession";
                            break;

                        case ServerExceptionMessage error:
                            throw new DownloadFileException(
                                $"Сервер сообщил об ошибке передачи файлов: {error.Text ?? "без описания"}");

                        case ServerMaintenanceMessage:
                            throw new ServerMaintenanceException();

                        default:
                            _logger?.LogWarning("Unexpected message type {Type} during download", payload.Length);
                            break;
                    }
                }
            }

            session.MarkCompleted();
            var unfinished = files.Where(f => !slots.TryGetValue(f.Id, out var slot) || !slot.Completed).ToList();
            if (unfinished.Count > 0)
                throw new DownloadFileException(
                    $"Сервер не передал файлы полностью (канал закрыт: {closeReason}): " +
                    string.Join(", ", unfinished.Select(f => f.Path)));
        }
        finally
        {
            foreach (var slot in slots.Values)
                slot.Dispose();
        }
    }

    private void WriteFrame(Dictionary<int, FileSlot> slots, Dictionary<int, FileRequest> byId, UploadFileMessage upload)
    {
        if (!slots.TryGetValue(upload.FileId, out var slot))
        {
            if (!byId.TryGetValue(upload.FileId, out var request))
                throw new DownloadFileException($"Сервер прислал кадр неизвестного файла id={upload.FileId}");
            slot = new FileSlot(request);
            slots[upload.FileId] = slot;
        }

        slot.Write(upload.Data, upload.Position);
        BytesReceived?.Invoke(upload.Data.Length);

        if (upload.LastFrame)
        {
            var md5Ok = slot.Complete();
            FileCompleted?.Invoke(slot.Request, md5Ok);
            if (!md5Ok)
                throw new DownloadFileException(
                    $"Файл {slot.Request.Path} скачан с неверной контрольной суммой (MD5 не совпал)");
        }
    }

    private void BroadcastState(bool active)
    {
        ChannelSession[] snapshot;
        lock (_sessionsLock)
        {
            snapshot = _sessions.Where(s => !s.Finished).ToArray();
        }
        foreach (var session in snapshot)
        {
            _ = session.SendAsync(new RequestChangeState(active), CancellationToken.None);
        }
    }

    /// <summary>
    /// Balances files across connections by size (largest first, into the
    /// least loaded bucket). The server does not care about the grouping;
    /// this just evens out the connection finish times.
    /// </summary>
    private static List<List<FileRequest>> SplitBySize(IReadOnlyList<FileRequest> files, int buckets)
    {
        var loads = new long[buckets];
        var result = Enumerable.Range(0, buckets).Select(_ => new List<FileRequest>()).ToArray();
        foreach (var file in files.OrderByDescending(f => f.Size))
        {
            var leastLoaded = 0;
            for (var i = 1; i < buckets; i++)
            {
                if (loads[i] < loads[leastLoaded])
                    leastLoaded = i;
            }
            result[leastLoaded].Add(file);
            loads[leastLoaded] += file.Size;
        }
        return [.. result.Where(b => b.Count > 0)];
    }

    public void Dispose()
    {
        lock (_sessionsLock)
        {
            foreach (var session in _sessions)
                session.Dispose();
            _sessions.Clear();
        }
    }

    /// <summary>One TCP+TLS channel; serializes writes because pause/resume frames come from other threads.</summary>
    private sealed class ChannelSession(SslStream stream) : IAsyncDisposable, IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public SslStream Stream { get; } = stream;

        public bool Finished { get; private set; }

        public void MarkCompleted() => Finished = true;

        public async ValueTask SendAsync(Message message, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                FrameCodec.WriteFrame(Stream, MessageCodec.Encode(message));
                await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            Finished = true;
            return Stream.DisposeAsync();
        }

        public void Dispose()
        {
            Finished = true;
            Stream.Dispose();
        }
    }

    /// <summary>An output file being assembled from positioned chunks, with incremental MD5.</summary>
    private sealed class FileSlot : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

        public FileSlot(FileRequest request)
        {
            Request = request;
            var directory = Path.GetDirectoryName(request.Path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            _handle = File.OpenHandle(
                request.Path, FileMode.Create, FileAccess.Write, FileShare.None,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
        }

        public FileRequest Request { get; }

        public bool Completed { get; private set; }

        public void Write(byte[] data, long position)
        {
            RandomAccess.Write(_handle, data, position);
            _md5.AppendData(data);
        }

        /// <summary>Computes the digest and returns true if it matches the manifest.</summary>
        public bool Complete()
        {
            var digest = _md5.GetHashAndReset();
            var ok = digest.AsSpan().SequenceEqual(Request.Md5);
            Completed = true;
            return ok;
        }

        public void Dispose()
        {
            _handle.Dispose();
            _md5.Dispose();
        }
    }
}
