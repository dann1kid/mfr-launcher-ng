using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mfr.Core.Network;
using Mfr.Core.Options;
using Mfr.Core.Storage;
using Mfr.Core.Tasks;
using Mfr.Core.V2;
using Mfr.Protocol.Dto;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// БТ-10.4: регион — сохранённый выбор игрока, иначе замер /ping; до выбора
/// региона запросов к серверу нет. Сохранённый регион применяется до пробы —
/// запрос уходит в выбранный регион, а не всегда в RU.
/// </summary>
public sealed class StartupNetworkTests : IDisposable
{
    private readonly string _gameFolder = Path.Combine(Path.GetTempPath(), $"mfr-net-{Guid.NewGuid():N}");
    private readonly LauncherServices _core;

    public StartupNetworkTests()
    {
        Directory.CreateDirectory(_gameFolder);
        _core = new LauncherServices(
            options: new LauncherOptions { Server = new ServerOptions { Address = "test.invalid" } },
            databasePath: Path.Combine(_gameFolder, "..", $"net-{Guid.NewGuid():N}.db"),
            gameFolder: _gameFolder);
    }

    public void Dispose() => _core.Dispose();

    private sealed class ScriptedServer(Func<HttpRequestMessage, HttpResponseMessage> script) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(script(request));
        }
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new VersionId("ok")))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
        },
    };

    private static string Path1(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.TrimStart('/');

    [Fact] // БТ-10.4: сохранённый регион применяется до пробы, без замера и без запросов
    public async Task Region_SavedChoice_AppliedWithoutPings()
    {
        _core.Repository.SetProperty(PropertyKeys.Location, "EU");
        var server = new ScriptedServer(request => Path1(request) == "v2/game/channels"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
                    new ChannelList([new ChannelDto("5.0")])))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
                },
            }
            : Ok());
        using var http = new HttpClient(server);
        var v2 = new LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));

        var region = await V2LifecycleService.ResolveRegionAsync(v2, CancellationToken.None);

        Assert.Equal(Region.EU, region);
        Assert.Equal(Region.EU, v2.Region);
        // замер не делался: единственный момент для /ping — отсутствие сохранённого выбора
        Assert.DoesNotContain(server.Requests, r => Path1(r) == "ping");
        // и первый запрос к серверу идёт уже в выбранный регион
        _ = await v2.Api.GetChannels(v2.Region);
        Assert.Equal("api2.test", server.Requests.Single().RequestUri!.Host);
    }

    [Fact] // БТ-10.4: выбора нет — замер обоих регионов, живой предпочитается, выбор сохраняется
    public async Task Region_NoSavedChoice_MeasuredAndPersisted()
    {
        // RU не отвечает, EU жив: победитель определён без гонки таймингов
        var server = new ScriptedServer(request =>
            Path1(request) == "ping" && request.RequestUri!.Host == "api.test"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Ok());
        using var http = new HttpClient(server);
        var v2 = new LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));

        var region = await V2LifecycleService.ResolveRegionAsync(v2, CancellationToken.None);

        Assert.Equal(Region.EU, region);
        Assert.Equal("EU", _core.Repository.GetProperty(PropertyKeys.Location));
        Assert.Equal(2, server.Requests.Count(r => Path1(r) == "ping")); // измерены оба
        Assert.Equal(1, server.Requests.Count(r => r.RequestUri!.Host == "api.test"));
        Assert.Equal(1, server.Requests.Count(r => r.RequestUri!.Host == "api2.test"));
    }

    [Fact] // БТ-10.4: испорченное сохранённое значение — как отсутствие выбора
    public async Task Region_CorruptSavedChoice_MeasuredAgain()
    {
        _core.Repository.SetProperty(PropertyKeys.Location, "XX");
        // EU не отвечает, RU жив
        var server = new ScriptedServer(request =>
            Path1(request) == "ping" && request.RequestUri!.Host == "api2.test"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Ok());
        using var http = new HttpClient(server);
        var v2 = new LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));

        var region = await V2LifecycleService.ResolveRegionAsync(v2, CancellationToken.None);

        Assert.Equal(Region.RU, region);
        Assert.Equal("RU", _core.Repository.GetProperty(PropertyKeys.Location));
        Assert.Equal(2, server.Requests.Count(r => Path1(r) == "ping"));
    }
}
