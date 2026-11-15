using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mfr.Core.Network;
using Mfr.Core.Options;
using Mfr.Core.Storage;
using Mfr.Core.Tasks;
using Mfr.Launcher.ViewModels;
using Mfr.Protocol.Dto;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// БТ-10/БТ-11: офлайн-режим — осознанный отказ от сети. На старте в офлайне клиент
/// не ходит к серверу и не предлагает сетевых действий; тумблер гасит кнопку сразу,
/// а возврат в online подключается без перезапуска. До установки на главной видна
/// линия, в которую пойдёт установка.
/// </summary>
public sealed class OfflineModeTests : IDisposable
{
    private readonly string _gameFolder = Path.Combine(Path.GetTempPath(), $"mfr-off-{Guid.NewGuid():N}");
    private readonly LauncherServices _core;

    public OfflineModeTests()
    {
        Directory.CreateDirectory(_gameFolder);
        _core = new LauncherServices(
            options: new LauncherOptions { Server = new ServerOptions { Address = "test.invalid" } },
            databasePath: Path.Combine(_gameFolder, "..", $"off-{Guid.NewGuid():N}.db"),
            gameFolder: _gameFolder,
            iniPath: Path.Combine(_gameFolder, "..", $"off-{Guid.NewGuid():N}.ini"));
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

    private static ScriptedServer Online(Func<HttpRequestMessage, HttpResponseMessage> extra) =>
        new(request => request.RequestUri!.AbsolutePath.TrimStart('/') switch
        {
            "ping" => Json(new VersionId("ok")),
            "v2/game/channels" => Json(new ChannelList([new ChannelDto("5.0")])),
            "v2/game/5.0/version" => Json(new VersionId("5.0.15")),
            "v2/launcher/version" => Json(new VersionId("9.9.9")),
            _ => extra(request),
        });

    [Fact] // БТ-11/БТ-10: офлайн-старт — ни одного запроса, сетевые действия не предлагаются
    public async Task OfflineStartup_NoServerRequests_NoNetworkActionsOffered()
    {
        _core.Repository.SetProperty(PropertyKeys.OnlineMode, "false");
        var server = Online(_ => Json(new VersionId("ok")));
        using var http = new HttpClient(server);
        var v2 = new Mfr.Core.V2.LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));
        var viewModel = new MainViewModel(_core, v2,
            tracePath: Path.Combine(Path.GetTempPath(), $"mfr-trace-{Guid.NewGuid():N}.log"));

        await viewModel.InitializeAsync();

        Assert.Empty(server.Requests); // офлайн — осознанный отказ от сети
        Assert.Equal(MainViewModel.UpdateStatus.DISABLE, viewModel.Status);
    }

    [Fact] // БТ-11: тумблер гасит кнопку сразу, возврат в online подключается без перезапуска
    public async Task OnlineToggle_DisablesActionsImmediately_AndReconnectsOnReturn()
    {
        var server = Online(_ => Json(new VersionId("ok")));
        using var http = new HttpClient(server);
        var v2 = new Mfr.Core.V2.LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));
        var viewModel = new MainViewModel(_core, v2,
            tracePath: Path.Combine(Path.GetTempPath(), $"mfr-trace-{Guid.NewGuid():N}.log"));
        await viewModel.InitializeAsync();
        Assert.Equal(MainViewModel.UpdateStatus.GAME_INSTALL, viewModel.Status);

        server.Requests.Clear();
        await viewModel.SwitchOnlineModeCommand.ExecuteAsync(null); // в офлайн

        Assert.Equal(MainViewModel.UpdateStatus.DISABLE, viewModel.Status);
        Assert.False(viewModel.LinesEnabled);
        Assert.Empty(server.Requests); // выключение сети не порождает запросов

        await viewModel.SwitchOnlineModeCommand.ExecuteAsync(null); // обратно в online

        // события опроса приходят из фоновых продолжений: статус доходит с задержкой
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (viewModel.Status != MainViewModel.UpdateStatus.GAME_INSTALL && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.Equal(MainViewModel.UpdateStatus.GAME_INSTALL, viewModel.Status);
        // подключение восстановлено сразу: замер доступности региона ушёл без перезапуска
        Assert.Contains(server.Requests, r => r.RequestUri!.AbsolutePath.Trim('/') == "ping");
    }

    [Fact] // БТ-8/БТ-11: до установки на главной видна линия установки, выбор меняет её сразу
    public async Task BeforeInstall_SelectedLineShownAsGameVersion()
    {
        var server = Online(_ => Json(new VersionId("ok")));
        using var http = new HttpClient(server);
        var v2 = new Mfr.Core.V2.LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));
        var viewModel = new MainViewModel(_core, v2,
            tracePath: Path.Combine(Path.GetTempPath(), $"mfr-trace-{Guid.NewGuid():N}.log"));

        await viewModel.InitializeAsync();

        Assert.Equal("5.0", viewModel.GameVersion); // линия вместо пустой версии игры

        viewModel.SelectedLine = "5.1"; // выбор до установки — молча, но видно сразу
        Assert.Equal("5.1", viewModel.GameVersion);
        Assert.Equal("5.1", _core.Repository.GetProperty(PropertyKeys.SelectedBuild));
    }
}
