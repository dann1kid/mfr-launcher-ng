using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mfr.Core.Network;
using Mfr.Core.Options;
using Mfr.Core.Storage;
using Mfr.Core.Tasks;
using Mfr.Core.V2;
using Xunit;
using ProtoSchema = Com.Lezenford.Mfr.Schema.V1.Schema;

namespace Mfr.Protocol.Tests;

/// <summary>
/// БТ-11/БТ-10.1: таймаут HTTP в ежечасном опросе — ошибка связи, а не пауза игрока
/// и не повод валить процесс: Elapsed-обработчик таймера — по сути async void, и
/// исключение из него уходит в пул потоков. Проход молча повторяется по тику.
/// </summary>
public sealed class LifecycleTimeoutTests : IDisposable
{
    private readonly string _gameFolder = Path.Combine(Path.GetTempPath(), $"mfr-tm-{Guid.NewGuid():N}");
    private readonly LauncherServices _core;

    public LifecycleTimeoutTests()
    {
        Directory.CreateDirectory(_gameFolder);
        _core = new LauncherServices(
            options: new LauncherOptions { Server = new ServerOptions { Address = "test.invalid" } },
            databasePath: Path.Combine(_gameFolder, "..", $"tm-{Guid.NewGuid():N}.db"),
            gameFolder: _gameFolder,
            iniPath: Path.Combine(_gameFolder, "..", $"tm-{Guid.NewGuid():N}.ini"));
    }

    public void Dispose() => _core.Dispose();

    private sealed class ScriptedServer(Func<HttpRequestMessage, Task<HttpResponseMessage>> script) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return script(request);
        }
    }

    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
        },
    };

    private static Task<HttpResponseMessage> TimedOut() =>
        Task.FromException<HttpResponseMessage>(
            new TaskCanceledException("The operation was canceled", new TimeoutException()));

    [Fact] // БТ-11: таймаут запроса версии в опросе не вылетает из обработчика таймера
    public async Task StatusRefresh_HttpTimeout_IsSwallowedUntilNextTick()
    {
        // установленная игра со схемой: опрос запрашивает версию своей линии,
        // и этот запрос виснет до таймаута — как медленный сервер
        V2Manifests.SaveSchemaFile(_core, new ProtoSchema
        {
            Version = "5.0.1",
        });
        _core.Repository.SetProperty(PropertyKeys.GameInstalled, "true");
        var server = new ScriptedServer(request => request.RequestUri!.AbsolutePath.TrimStart('/') switch
        {
            "ping" => Task.FromResult(Json(new Mfr.Protocol.Dto.VersionId("ok"))),
            "v2/game/channels" => Task.FromResult(Json(new Mfr.Protocol.Dto.ChannelList(
                [new Mfr.Protocol.Dto.ChannelDto("5.0")]))),
            "v2/game/5.0/version" => TimedOut(),
            "v2/launcher/version" => Task.FromResult(Json(new Mfr.Protocol.Dto.VersionId("9.9.9"))),
            _ => Task.FromResult(Json(new Mfr.Protocol.Dto.VersionId("ok"))),
        });
        using var http = new HttpClient(server);
        var v2 = new LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));
        using var lifecycle = new V2LifecycleService(v2);
        lifecycle.OnGameInstalled(); // поднимает InstalledSchema и признак установки

        // смена региона проверяет связь и запускает проход опроса
        var exception = await Record.ExceptionAsync(
            () => lifecycle.ChangeRegionAsync(Mfr.Protocol.Dto.Region.RU));

        Assert.Null(exception); // проход тихо провалился, следующий тик повторит
        Assert.Contains(server.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("5.0/version"));
    }
}
