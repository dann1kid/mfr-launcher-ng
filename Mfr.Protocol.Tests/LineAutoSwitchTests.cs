using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mfr.Core.Exceptions;
using Mfr.Core.Network;
using Mfr.Core.Options;
using Mfr.Core.Storage;
using Mfr.Core.Tasks;
using Mfr.Core.V2;
using Mfr.Protocol.Dto;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// К-2 без установленной игры (К-2.2.б) и К-3 без игры: ответ на запрос версии
/// выбранной линии распознаётся и до установки — снятая линия не приводит к
/// бесконечным падениям установки, «пока нечего отдать» гасит кнопку установки.
/// </summary>
public sealed class LineAutoSwitchTests : IDisposable
{
    private readonly string _gameFolder = Path.Combine(Path.GetTempPath(), $"mfr-line-{Guid.NewGuid():N}");
    private readonly LauncherServices _core;

    public LineAutoSwitchTests()
    {
        Directory.CreateDirectory(_gameFolder);
        _core = new LauncherServices(
            options: new LauncherOptions { Server = new ServerOptions { Address = "test.invalid" } },
            databasePath: Path.Combine(_gameFolder, "..", $"line-{Guid.NewGuid():N}.db"),
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

    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
        },
    };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code)
    {
        Content = new ByteArrayContent([]),
    };

    private static string Path1(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.TrimStart('/');

    private (ScriptedServer Server, LauncherServicesV2 V2) Compose(
        Func<HttpRequestMessage, HttpResponseMessage> script)
    {
        var server = new ScriptedServer(script);
        var http = new HttpClient(server);
        return (server, new LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http)));
    }

    [Fact] // К-2.2.б: без игры снятая линия распознаётся опросом — перевод на первую доступную
    public async Task StatusRefresh_WithoutGame_GoneLineAutoSwitchesToFirstAvailable()
    {
        _core.Repository.SetProperty(PropertyKeys.SelectedBuild, "5.0");
        var (server, v2) = Compose(request => Path1(request) switch
        {
            "ping" => Json(new VersionId("ok")),
            "v2/game/channels" => Json(new ChannelList([new ChannelDto("6.0")])),
            "v2/game/5.0/version" => Status(HttpStatusCode.NotFound),
            "v2/launcher/version" => Json(new VersionId("9.9.9")),
            _ => Status(HttpStatusCode.NotFound),
        });
        using var lifecycle = new V2LifecycleService(v2);
        V2LifecycleService.LineAutoChange? change = null;
        lifecycle.LineAutoChanged += value => change = value;

        await lifecycle.InitializeAsync();

        // версия своей линии запрошена, линия переведена на первую доступную
        Assert.Contains(server.Requests, r => Path1(r) == "v2/game/5.0/version");
        Assert.Equal("6.0", _core.Repository.GetProperty(PropertyKeys.SelectedBuild));
        Assert.NotNull(change);
        Assert.Equal(("5.0", "6.0", false), (change!.OldLine, change.NewLine, change.GameInstalled));
        Assert.False(lifecycle.LineHasNothing);
    }

    [Fact] // К-3 без игры: «пока нечего отдать» распознаётся и до установки
    public async Task StatusRefresh_WithoutGame_NothingYetBlocksInstallOffer()
    {
        _core.Repository.SetProperty(PropertyKeys.SelectedBuild, "5.0");
        var (server, v2) = Compose(request => Path1(request) switch
        {
            "ping" => Json(new VersionId("ok")),
            "v2/game/channels" => Json(new ChannelList([new ChannelDto("5.0")])),
            "v2/game/5.0/version" => Status(HttpStatusCode.ServiceUnavailable),
            "v2/launcher/version" => Json(new VersionId("9.9.9")),
            _ => Status(HttpStatusCode.NotFound),
        });
        using var lifecycle = new V2LifecycleService(v2);

        await lifecycle.InitializeAsync();

        Assert.True(lifecycle.LineHasNothing);
        Assert.Equal("5.0", _core.Repository.GetProperty(PropertyKeys.SelectedBuild)); // линия не меняется
    }

    [Fact] // К-2.2.б: установка в снятую линию переводит клиента — повторный клик ставит в новую
    public async Task Install_GoneLine_SwitchesToFirstAvailable_AndFailsWithClearMessage()
    {
        _core.Repository.SetProperty(PropertyKeys.SelectedBuild, "5.0");
        var (server, v2) = Compose(request => Path1(request) switch
        {
            "v2/game/channels" => Json(new ChannelList([new ChannelDto("6.0")])),
            "v2/game/5.0/version" => Status(HttpStatusCode.NotFound),
            _ => Status(HttpStatusCode.NotFound),
        });

        var failure = await Assert.ThrowsAsync<TaskExecuteException>(
            () => new GameInstallV2Task(v2).Execute(null));

        Assert.IsType<LineUnavailableException>(failure.InnerException);
        Assert.Equal("6.0", _core.Repository.GetProperty(PropertyKeys.SelectedBuild));
    }
}
