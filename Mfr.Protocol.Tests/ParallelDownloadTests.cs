using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Mfr.Core.Network;
using Mfr.Core.V2;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// БТ-1.6: крупный объект качается несколькими диапазонами параллельно; пауза
/// и обрыв продолжают каждый диапазон со своего места; сервер без диапазонов,
/// объект без размера и общий бюджет соединений остаются на своих путях;
/// ход загрузок пишется в downloads.log.
/// </summary>
public sealed class ParallelDownloadTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"mfr-pdl-{Guid.NewGuid():N}");
    private readonly string _objectName = $"big-{Guid.NewGuid():N}.obj";

    public ParallelDownloadTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Сервер, понимающий Range и запоминающий каждый запрос.</summary>
    private sealed class RangeServer(byte[] content) : HttpMessageHandler
    {
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

    /// <summary>Сервер без диапазонов: любой запрос — полный объект кодом 200.</summary>
    private sealed class NoRangeServer(byte[] content) : HttpMessageHandler
    {
        public List<string> Moments { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Moments)
            {
                Moments.Add("full");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        }
    }

    /// <summary>Медленный сервер: держит ответ, чтобы видеть реальную параллельность.</summary>
    private sealed class SlowRangeServer(byte[] content) : HttpMessageHandler
    {
        public int InFlight;
        public int MaxInFlight;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref InFlight);
            int nowMax;
            do
            {
                nowMax = Volatile.Read(ref MaxInFlight);
                if (current <= nowMax)
                {
                    break;
                }
            } while (Interlocked.CompareExchange(ref MaxInFlight, current, nowMax) != nowMax);
            try
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                var range = request.Headers.Range?.Ranges.FirstOrDefault();
                var from = range?.From ?? 0;
                var to = Math.Min(range?.To ?? content.Length - 1, content.Length - 1);
                var slice = new byte[to - from + 1];
                Array.Copy(content, from, slice, 0, slice.Length);
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice),
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
                return response;
            }
            finally
            {
                Interlocked.Decrement(ref InFlight);
            }
        }
    }

    private static byte[] BigContent(int megabytes, int seed)
    {
        var bytes = new byte[megabytes * 1024L * 1024];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private string Target(string name) => Path.Combine(_folder, name);

    private static long ParseFrom(string range)
    {
        // "bytes {from}-{to}"
        var text = range["bytes ".Length..];
        return long.Parse(text[..text.IndexOf('-')]);
    }

    [Fact] // БТ-1.6: крупный объект качается несколькими диапазонами, не одним запросом
    public async Task BigFile_DownloadsByParallelRanges()
    {
        var content = BigContent(33, 42); // выше порога 32 МиБ
        var server = new RangeServer(content);
        using var http = new HttpClient(server);
        var downloader = new HttpFileDownloader(http);
        var target = Target("big.pack");

        await downloader.DownloadToFileAsync("https://storage.test", _objectName, target, segmentHint: 4);

        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        var starts = server.Order.Where(o => o.Moment == "range" && o.Range != "bytes 0-0")
            .Select(o => ParseFrom(o.Range)).ToList();
        Assert.True(starts.Distinct().Count() >= 3,
            $"диапазонов со своими стартами должно быть несколько, было: {string.Join(",", starts)}");
    }

    [Fact] // БТ-1.6/БТ-1.1: отмена посреди загрузки — каждый диапазон продолжается со своего места
    public async Task BigFile_CancelledMidway_ResumesPerSegment()
    {
        var content = BigContent(33, 7);
        var server = new RangeServer(content);
        using var http = new HttpClient(server);
        var downloader = new HttpFileDownloader(http);
        var target = Target("resumed.pack");
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
            await downloader.DownloadToFileAsync("https://storage.test", _objectName, target, cancel.Token, segmentHint: 4);
        }
        catch (Exception exception)
        {
            Assert.True(exception is OperationCanceledException,
                $"ожидалась отмена, было {exception.GetType().Name}: {exception.Message}");
        }
        Assert.True(File.Exists(target + ".part"), "частичный файл остаётся для докачки");
        Assert.True(File.Exists(target + ".part.segs"), "прогресс диапазонов сохранён");

        // точки возобновления — из sidecar: границы диапазонов плюс сделанное
        var sidecar = await File.ReadAllLinesAsync(target + ".part.segs");
        var total = long.Parse(sidecar[0]);
        var segments = sidecar.Length - 1;
        var done = sidecar.Skip(1).Select(long.Parse).ToArray();
        var expectedStarts = new HashSet<long>();
        for (var i = 0; i < segments; i++)
        {
            var from = total * i / segments;
            var length = total * (i + 1) / segments - from;
            if (done[i] < length)
            {
                expectedStarts.Add(from + done[i]); // каждый диапазон — строго со своего места
            }
        }
        var orderBeforeResume = server.Order.Count;

        await downloader.DownloadToFileAsync("https://storage.test", _objectName, target, segmentHint: 2); // подсказка сменилась

        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        var resumeStarts = server.Order.Skip(orderBeforeResume)
            .Where(o => o.Moment == "range" && o.Range != "bytes 0-0")
            .Select(o => ParseFrom(o.Range)).ToHashSet();
        Assert.True(expectedStarts.SetEquals(resumeStarts),
            $"диапазоны продолжились не со своих мест: ждали [{string.Join(",", expectedStarts)}], было [{string.Join(",", resumeStarts)}]");
    }

    [Fact] // БТ-1.6: мелкий объект — только проба 0-0, без нарезки
    public async Task SmallFile_SingleRequestNoRanges()
    {
        var content = "small"u8.ToArray();
        var server = new RangeServer(content);
        using var http = new HttpClient(server);
        var downloader = new HttpFileDownloader(http);
        var target = Target("small.dat");

        await downloader.DownloadToFileAsync("https://storage.test", _objectName, target, segmentHint: 4);

        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        Assert.True(server.Order.All(entry => entry.Moment != "range" || entry.Range == "bytes 0-0"),
            $"мелкий объект — только проба 0-0, было: {string.Join(" | ", server.Order.Select(o => $"{o.Moment}:{o.Range}"))}");
    }

    [Fact] // БТ-1.6: сервер без диапазонов — обычный однопоточный путь
    public async Task NoRangeServer_FallsBackToSingleStream()
    {
        var content = BigContent(33, 11);
        var server = new NoRangeServer(content);
        using var http = new HttpClient(server);
        var downloader = new HttpFileDownloader(http);
        var target = Target("norange.pack");

        await downloader.DownloadToFileAsync("https://storage.test", _objectName, target, segmentHint: 4);

        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        Assert.Equal(2, server.Moments.Count); // проба и сама загрузка — оба полным объектом
    }

    [Fact] // БТ-1.6: объект без размера (подсказка 1) — один запрос, без пробы диапазонами
    public async Task BigFile_HintOne_NoSegmentation()
    {
        var content = BigContent(33, 13);
        var server = new RangeServer(content);
        using var http = new HttpClient(server);
        var downloader = new HttpFileDownloader(http);
        var target = Target("nohint.pack");

        await downloader.DownloadToFileAsync("https://storage.test", _objectName, target, segmentHint: 1);

        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        Assert.Single(server.Order, entry => entry.Moment == "full");
    }

    [Fact] // БТ-1.6: общий бюджет соединений делится между файлами
    public async Task Budget_TwoBigFiles_ConcurrentStreamsCapped()
    {
        var content = BigContent(33, 17);
        var server = new SlowRangeServer(content);
        using var http = new HttpClient(server);
        var downloader = new HttpFileDownloader(http, maxParallelStreams: 2);

        await Task.WhenAll(
            downloader.DownloadToFileAsync("https://storage.test", "one.obj", Target("one.pack"), segmentHint: 4),
            downloader.DownloadToFileAsync("https://storage.test", "two.obj", Target("two.pack"), segmentHint: 4));

        Assert.Equal(content, await File.ReadAllBytesAsync(Target("one.pack")));
        Assert.Equal(content, await File.ReadAllBytesAsync(Target("two.pack")));
        Assert.True(server.MaxInFlight <= 2,
            $"бюджет двух соединений превышен: {server.MaxInFlight}");
        Assert.True(server.MaxInFlight >= 2, "параллельность должна использоваться");
    }

    [Fact] // БТ-1.6: осиротевший sidecar (.part удалён игроком или антивирусом) — загрузка самооздоравливается
    public async Task OrphanedSidecar_WithoutPart_RestartsFromScratch()
    {
        var content = BigContent(33, 23);
        var server = new RangeServer(content);
        using var http = new HttpClient(server);
        var downloader = new HttpFileDownloader(http);
        var target = Target("orphan.pack");
        using var cancel = new CancellationTokenSource();
        var received = 0L;
        downloader.BytesReceived += _ =>
        {
            if (Interlocked.Increment(ref received) == 3 && !cancel.IsCancellationRequested)
            {
                cancel.Cancel();
            }
        };

        try
        {
            await downloader.DownloadToFileAsync("https://storage.test", _objectName, target, cancel.Token, segmentHint: 4);
        }
        catch (OperationCanceledException)
        {
            // пауза игрока
        }
        Assert.True(File.Exists(target + ".part.segs"));
        File.Delete(target + ".part"); // игрок освободил место, sidecar остался

        await downloader.DownloadToFileAsync("https://storage.test", _objectName, target, segmentHint: 4);

        Assert.Equal(content, await File.ReadAllBytesAsync(target)); // пара part+sidecar перезапущена с нуля
        Assert.False(File.Exists(target + ".part.segs"));
    }

    [Fact] // БТ-1.6: задача даёт подсказку по размеру — крупный нарезкой, мелкий одним запросом
    public async Task Task_SizeToHint_BigFileSegmentedSmallSingle()
    {
        var big = BigContent(33, 29);
        var small = "tiny"u8.ToArray();
        var requests = new ConcurrentBag<(string Path, string Moment, string Range)>();
        var server = new RecordingRangeServer(big, small, requests);
        using var http = new HttpClient(server);
        using var core = new Mfr.Core.Tasks.LauncherServices(
            options: new Mfr.Core.Options.LauncherOptions
            {
                Server = new Mfr.Core.Options.ServerOptions { Address = "test.invalid", ConnectionCount = 8 },
            },
            databasePath: Path.Combine(_folder, "pdl.db"),
            gameFolder: _folder,
            iniPath: Path.Combine(_folder, "pdl.ini"));
        var v2 = new Mfr.Core.V2.LauncherServicesV2(core,
            new Mfr.Core.Network.ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));
        var task = new Mfr.Core.V2.DownloadFilesV2Task(v2);
        var bigPath = "opt/Big/big.pack";
        var smallPath = "opt/Small/small.dat";
        var parameters = new Mfr.Core.V2.V2DownloadParameters("https://storage.test",
        [
            new Mfr.Core.V2.V2FilePlan(bigPath, null, "big.obj", null,
                System.Security.Cryptography.SHA256.HashData(big), "", (ulong)big.LongLength),
            new Mfr.Core.V2.V2FilePlan(smallPath, null, "small.obj", null,
                System.Security.Cryptography.SHA256.HashData(small), "", (ulong)small.Length),
        ], ApplyOptionalPath: false);

        await task.Execute(parameters);

        Assert.Equal(big, await File.ReadAllBytesAsync(Path.Combine(_folder, bigPath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(small, await File.ReadAllBytesAsync(Path.Combine(_folder, smallPath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Contains(requests, r => r.Path == "big.obj" && r.Moment == "range" && r.Range != "bytes 0-0"); // нарезан
        Assert.Contains(requests, r => r.Path == "small.obj" && r.Moment == "full"); // одним запросом
    }

    [Fact] // БТ-11: ограничение скорости держит среднюю — три мегабайта при лимите 1 МБ/с дольше двух секунд
    public async Task SpeedLimit_ThrottlesAverageRate()
    {
        var content = new byte[3 * 1024 * 1024]; // первое «ведро» в секунду лимита пропускает мгновенно — дальше троттель
        new Random(31).NextBytes(content);
        var server = new RangeServer(content);
        using var http = new HttpClient(server);
        // лимит включается после создания загрузчика — как из настроек на работающем клиенте
        var downloader = new HttpFileDownloader(http);
        downloader.SetSpeedLimit(1024 * 1024);
        var target = Target("limited.pack");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await downloader.DownloadToFileAsync("https://storage.test", "limited.obj", target);

        watch.Stop();
        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(1800),
            $"три мегабайта скачались за {watch.ElapsedMilliseconds} мс при лимите 1 МБ/с — лимит не держится");
    }

    private sealed class RecordingRangeServer(byte[] big, byte[] small,
        ConcurrentBag<(string Path, string Moment, string Range)> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var name = request.RequestUri!.AbsolutePath.TrimStart('/');
            var content = name == "big.obj" ? big : small;
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range is null)
            {
                requests.Add((name, "full", ""));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content),
                });
            }
            var from = range.From ?? 0;
            var to = Math.Min(range.To ?? content.Length - 1, content.Length - 1);
            requests.Add((name, "range", $"bytes {from}-{to}"));
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

    [Fact] // БТ-1.6: ход загрузок пишется в downloads.log рядом с exe
    public async Task DownloadLog_RecordsFileProgress()
    {
        var content = BigContent(33, 19);
        var server = new RangeServer(content);
        using var http = new HttpClient(server);
        var downloader = new HttpFileDownloader(http);
        var target = Target("logged.pack");

        await downloader.DownloadToFileAsync("https://storage.test", _objectName, target, segmentHint: 4);

        var logPath = Path.Combine(AppContext.BaseDirectory, "downloads.log");
        Assert.True(File.Exists(logPath), "журнал загрузок создаётся рядом с exe");
        var log = await File.ReadAllTextAsync(logPath);
        Assert.Contains(_objectName, log); // нарезка записана с именем объекта
    }
}
