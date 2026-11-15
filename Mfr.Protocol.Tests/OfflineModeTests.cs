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

        Assert.Equal(MainViewModel.UpdateStatus.GAME_INSTALL, viewModel.Status);
        // подключение восстановлено сразу: замер доступности региона ушёл без перезапуска
        Assert.Contains(server.Requests, r => r.RequestUri!.AbsolutePath.Trim('/') == "ping");
    }

    [Fact] // по решению владельца: до установки строка версии игры пустая, линию там не показываем
    public async Task BeforeInstall_GameVersionStaysEmpty()
    {
        var server = Online(_ => Json(new VersionId("ok")));
        using var http = new HttpClient(server);
        var v2 = new Mfr.Core.V2.LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));
        var viewModel = new MainViewModel(_core, v2,
            tracePath: Path.Combine(Path.GetTempPath(), $"mfr-trace-{Guid.NewGuid():N}.log"));

        await viewModel.InitializeAsync();

        Assert.True(string.IsNullOrEmpty(viewModel.GameVersion));

        viewModel.SelectedLine = "5.1"; // выбор до установки строку версии не занимает
        Assert.True(string.IsNullOrEmpty(viewModel.GameVersion));
        Assert.Equal("5.1", _core.Repository.GetProperty(PropertyKeys.SelectedBuild));
    }

    [Fact] // офлайн — отказ от сети: идущая задача останавливается переключением режима
    public async Task OfflineToggle_CancelsRunningTask()
    {
        var server = Online(_ => Json(new VersionId("ok")));
        using var http = new HttpClient(server);
        var v2 = new Mfr.Core.V2.LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));
        var viewModel = new MainViewModel(_core, v2,
            tracePath: Path.Combine(Path.GetTempPath(), $"mfr-trace-{Guid.NewGuid():N}.log"));
        await viewModel.InitializeAsync();

        var running = viewModel.ExecuteTask(new IdleTask(_core),
            ct => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        try
        {
            await viewModel.SwitchOnlineModeCommand.ExecuteAsync(null); // в офлайн
        }
        finally
        {
            // задача снята отменой, а не молотит вечно
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(MainViewModel.UpdateStatus.DISABLE, viewModel.Status);
    }

    [Fact] // БТ-10.3: после офлайн-старта возврат в online при недоступном сервере —
           // честный «Сервер недоступен» с повтором, а не «Обновлений нет» навсегда
    public async Task OfflineStart_OnlineToggleWithDeadServer_ShowsBlockNotIdle()
    {
        _core.Repository.SetProperty(PropertyKeys.OnlineMode, "false");
        var server = new ScriptedServer(request =>
            new HttpResponseMessage(HttpStatusCode.NotFound)); // сервер отвечает отказом
        using var http = new HttpClient(server);
        var v2 = new Mfr.Core.V2.LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));
        var viewModel = new MainViewModel(_core, v2,
            tracePath: Path.Combine(Path.GetTempPath(), $"mfr-trace-{Guid.NewGuid():N}.log"));
        await viewModel.InitializeAsync();
        Assert.Equal(MainViewModel.UpdateStatus.DISABLE, viewModel.Status);

        await viewModel.SwitchOnlineModeCommand.ExecuteAsync(null); // в online, сервер мёртв

        Assert.Equal(MainViewModel.UpdateStatus.BLOCK, viewModel.Status); // повторяем подключение
        Assert.Contains("недоступен", viewModel.Description);
    }

    /// <summary>Пустая задача: ходом управляет тест через делегат запуска.</summary>
    private sealed class IdleTask(LauncherServices services) : LauncherTask<object?, object?>(services)
    {
        protected override Task<object?> Action(object? parameters, CancellationToken ct) =>
            Task.FromResult<object?>(null);
    }
}
