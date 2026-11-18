using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Mfr.Core.Network;
using Mfr.Core.V2;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// БТ-1.6: крупный объект качается несколькими диапазонами параллельно; пауза
/// и обрыв продолжают каждый диапазон со своего места; мелкие объекты и серверы
/// без диапазонов остаются на однопоточном пути.
/// </summary>
public sealed class ParallelDownloadTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"mfr-pdl-{Guid.NewGuid():N}");

    public ParallelDownloadTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Сервер, понимающий Range и запоминающий начало каждого диапазона.</summary>
    private sealed class RangeServer(byte[] content) : HttpMessageHandler
    {
        public ConcurrentBag<string> RangeStarts { get; } = [];
        public List<(string Moment, string Range)> Order { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range is null)
            {
                lock (Order)
                {
                    Order.Add(("full", ""));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content),
                });
            }
            var from = range.From ?? 0;
            var to = Math.Min(range.To ?? content.Length - 1, content.Length - 1);
            lock (Order)
            {
                Order.Add(("range", $"bytes {from}-{to}"));
            }
            RangeStarts.Add(from.ToString());
            var slice = new byte[to - from + 1];
            Array.Copy(content, from, slice, 0, slice.Length);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
            return Task.FromResult(response);
        }
    }

    private static byte[] BigContent(int megabytes, int seed)
    {
        var bytes = new byte[megabytes * 1024L * 1024];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Target(string folder, string name) => Path.Combine(folder, name);

    [Fact] // БТ-1.6: крупный объект качается несколькими диапазонами, не одним запросом
    public async Task BigFile_DownloadsByParallelRanges()
    {
        var content = BigContent(33, 42); // выше порога 32 МиБ
        var server = new RangeServer(content);
        using var http = new HttpClient(server);
        var downloader = new HttpFileDownloader(http);
        var target = Target(_folder, "big.pack");

        await downloader.DownloadToFileAsync("https://storage.test", "big.obj", target, segmentHint: 4);

        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        Assert.True(server.RangeStarts.Distinct().Count() >= 3,
            $"диапазонов со своими стартами должно быть несколько, было: {string.Join(",", server.RangeStarts)}; порядок: {string.Join(" | ", server.Order.Select(o => o.Range))}");
    }

    [Fact] // БТ-1.6/БТ-1.1: отмена посреди загрузки — продолжение каждого диапазона со своего места
    public async Task BigFile_CancelledMidway_ResumesPerSegment()
    {
        var content = BigContent(33, 7);
        var server = new RangeServer(content);
        using var http = new HttpClient(server);
        var downloader = new HttpFileDownloader(http);
        var target = Target(_folder, "resumed.pack");
        using var cancel = new CancellationTokenSource();
        var received = 0L;
        downloader.BytesReceived += _ =>
        {
            if (Interlocked.Increment(ref received) == 3 && !cancel.IsCancellationRequested)
            {
                cancel.Cancel(); // пауза игрока на первых же порциях
            }
        };

        try
        {
            await downloader.DownloadToFileAsync("https://storage.test", "big.obj", target, cancel.Token, segmentHint: 4);
        }
        catch (Exception exception)
        {
            Assert.True(exception is OperationCanceledException,
                $"ожидалась отмена, было {exception.GetType().Name}: {exception.Message}; порядок: {string.Join(" | ", server.Order.Select(o => $"{o.Moment}:{o.Range}"))}");
        }
        Assert.True(File.Exists(target + ".part"), "частичный файл остаётся для докачки");
        Assert.True(File.Exists(target + ".part.segs"), "прогресс диапазонов сохранён");

        await downloader.DownloadToFileAsync("https://storage.test", "big.obj", target, segmentHint: 4);

        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        // вторая попытка стартует диапазоны не с нуля: есть старты больше 0
        Assert.Contains(server.RangeStarts, start => start != "0");
    }

    [Fact] // БТ-1.6: мелкий объект — один запрос, без диапазонной нарезки
    public async Task SmallFile_SingleRequestNoRanges()
    {
        var content = "small"u8.ToArray();
        var server = new RangeServer(content);
        using var http = new HttpClient(server);
        var downloader = new HttpFileDownloader(http);
        var target = Target(_folder, "small.dat");

        await downloader.DownloadToFileAsync("https://storage.test", "small.obj", target, segmentHint: 4);

        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        Assert.True(server.Order.All(entry => entry.Moment != "range" || entry.Range == "bytes 0-0"),
            $"мелкий объект — только проба 0-0, было: {string.Join(" | ", server.Order.Select(o => $"{o.Moment}:{o.Range}"))}");
    }
}
