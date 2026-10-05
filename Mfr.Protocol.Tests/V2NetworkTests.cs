using System.Net;
using Mfr.Core.Exceptions;
using Mfr.Core.Network;
using Mfr.Core.Storage;
using Mfr.Core.V2;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>v2 network machinery: Range resume, restart, stall watchdog, gzip manifests,
/// version lines and the H2 migration scanner.</summary>
public class V2NetworkTests
{
    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> script) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(script(request));
        }
    }

    private static HttpResponseMessage Bytes(byte[] content, HttpStatusCode code = HttpStatusCode.OK) => new()
    {
        StatusCode = code,
        Content = new ByteArrayContent(content),
    };

    private static string TempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mfrtest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    [Fact]
    public async Task RangeResume_AppendsToPart()
    {
        var target = TempPath();
        await File.WriteAllTextAsync(target + ".part", "prefix");
        var handler = new ScriptedHandler(request =>
            request.Headers.Range is null
                ? Bytes("prefix-suffix"u8.ToArray())
                : Bytes("suffix"u8.ToArray(), HttpStatusCode.PartialContent));
        var downloader = new HttpFileDownloader(new HttpClient(handler));

        await downloader.DownloadToFileAsync("https://x", "obj", target);

        Assert.Equal("prefixsuffix", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + ".part"));
        var rangeCall = handler.Requests.Single(r => r.Headers.Range is not null);
        Assert.Equal(6, rangeCall.Headers.Range!.Ranges.Single().From);
    }

    [Fact]
    public async Task RangeNotSatisfiable_KeepsPartAsResult()
    {
        var target = TempPath();
        await File.WriteAllTextAsync(target + ".part", "full-payload");
        var handler = new ScriptedHandler(_ => Bytes(Array.Empty<byte>(), HttpStatusCode.RequestedRangeNotSatisfiable));

        await new HttpFileDownloader(new HttpClient(handler)).DownloadToFileAsync("https://x", "obj", target);

        Assert.Equal("full-payload", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task PlainSuccess_RestartsFromScratch()
    {
        var target = TempPath();
        await File.WriteAllTextAsync(target + ".part", "stale garbage");
        var handler = new ScriptedHandler(_ => Bytes("fresh"u8.ToArray()));

        await new HttpFileDownloader(new HttpClient(handler)).DownloadToFileAsync("https://x", "obj", target);

        Assert.Equal("fresh", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task ServerError_ThrowsDownloadException()
    {
        var handler = new ScriptedHandler(_ => Bytes(Array.Empty<byte>(), HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<DownloadFileException>(() =>
            new HttpFileDownloader(new HttpClient(handler)).DownloadToFileAsync("https://x", "obj", TempPath()));
    }

    private sealed class NeverEndingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task StalledStream_TriggersWatchdog()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NeverEndingStream()),
        });
        var downloader = new HttpFileDownloader(new HttpClient(handler), stallTimeout: TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAsync<DownloadStalledException>(() =>
            downloader.DownloadToFileAsync("https://x", "obj", TempPath()));
    }

    [Fact]
    public async Task Manifest_GzipWithRawFallback()
    {
        var raw = "manifest-bytes"u8.ToArray();
        using var gz = new MemoryStream();
        await using (var packer = new System.IO.Compression.GZipStream(gz, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            await packer.WriteAsync(raw);
        }
        var goodGz = gz.ToArray();

        var good = new ScriptedHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith(".gz")
                ? Bytes(goodGz)
                : Bytes(raw));
        var manifest = await new HttpFileDownloader(new HttpClient(good))
            .DownloadManifestAsync("https://x", "raw.proto", "packed.proto.gz");
        Assert.Equal(raw, manifest);
        Assert.True(good.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith(".gz")) is not null);

        // broken gzip copy falls back to the raw object
        var broken = new ScriptedHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith(".gz")
                ? Bytes("not-gzip"u8.ToArray())
                : Bytes(raw));
        var rawManifest = await new HttpFileDownloader(new HttpClient(broken))
            .DownloadManifestAsync("https://x", "raw.proto", "packed.proto.gz");
        Assert.Equal(raw, rawManifest);
    }

    [Fact]
    public void VersionLine_FirstTwoOctets()
    {
        Assert.Equal("5.0", VersionLine.Of("5.0.15"));
        Assert.Equal("5.0", VersionLine.Of("5.0"));
        Assert.Null(VersionLine.Of("garbage"));
        Assert.Null(VersionLine.Of(null));
        Assert.Equal(0, VersionLine.Compare("5.0", "5.0"));
        Assert.True(VersionLine.Compare("5.1", "5.0") > 0);
        Assert.True(VersionLine.Compare("5.10", "5.9") > 0); // numeric octets, not lexicographic
        Assert.True(VersionLine.Compare("4.9", "5.0") < 0);
    }

    [Fact]
    public void H2Migrator_ShapeChecksRejectBookkeeping()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mv_{Guid.NewGuid():N}.db");
        // a marker byte + value, then a later page repeating the key with junk digits
        var fixture = new List<byte>();
        fixture.AddRange("SELECTED_BUILD"u8); fixture.Add(0x06); fixture.AddRange("5.0"u8); fixture.Add(0);
        fixture.AddRange("LOCATION"u8); fixture.Add(0x02); fixture.AddRange("RU"u8); fixture.Add(0);
        fixture.AddRange("SELECTED_BUILD"u8); fixture.AddRange(new byte[] { 0x06, 0x06, 0x06 }); // later bookkeeping page
        File.WriteAllBytes(path, fixture.ToArray());

        var map = H2Migrator.ReadProperties(path);

        Assert.Equal("5.0", map[PropertyKeys.SelectedBuild]);
        Assert.Equal("RU", map[PropertyKeys.Location]);
        File.Delete(path);
    }
}
