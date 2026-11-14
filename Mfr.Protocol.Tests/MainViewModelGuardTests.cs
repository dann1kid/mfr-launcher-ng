using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mfr.Core.Network;
using Mfr.Core.Options;
using Mfr.Core.Tasks;
using Mfr.Launcher.ViewModels;
using Mfr.Protocol.Dto;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// К-1.2: во время задачи главная кнопка показывает ход задачи (Блок/Пауза),
/// и события опроса сервера — связь, статусы, регион — не возвращают ей действие
/// посреди работы: повторный клик невозможен, вторая задача не предлагается.
/// </summary>
public sealed class MainViewModelGuardTests : IDisposable
{
    private readonly string _gameFolder = Path.Combine(Path.GetTempPath(), $"mfr-vm-{Guid.NewGuid():N}");
    private readonly LauncherServices _core;

    public MainViewModelGuardTests()
    {
        Directory.CreateDirectory(_gameFolder);
        _core = new LauncherServices(
            options: new LauncherOptions { Server = new ServerOptions { Address = "test.invalid" } },
            databasePath: Path.Combine(_gameFolder, "..", $"vm-{Guid.NewGuid():N}.db"),
            gameFolder: _gameFolder,
            iniPath: Path.Combine(_gameFolder, "..", $"vm-{Guid.NewGuid():N}.ini"));
    }

    public void Dispose() => _core.Dispose();

    private sealed class ScriptedServer(Func<HttpRequestMessage, HttpResponseMessage> script) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
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

    [Fact]
    public async Task TaskRunning_ButtonKeepsTaskState_AgainstStatusRefresh()
    {
        var server = new ScriptedServer(request => request.RequestUri!.AbsolutePath.TrimStart('/') switch
        {
            "ping" => Json(new VersionId("ok")),
            "v2/game/channels" => Json(new ChannelList([new ChannelDto("5.0")])),
            "v2/game/5.0/version" => Json(new VersionId("5.0.15")),
            "v2/launcher/version" => Json(new VersionId("9.9.9")),
            _ => Json(new VersionId("ok")),
        });
        using var http = new HttpClient(server);
        var v2 = new Mfr.Core.V2.LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));
        var viewModel = new MainViewModel(_core, v2);

        await viewModel.InitializeAsync();

        // игры нет: кнопка предлагает установку
        Assert.Equal(MainViewModel.UpdateStatus.GAME_INSTALL, viewModel.Status);

        // задача идёт: удерживаем её открытой и проверяем кнопку в ходе задачи
        var release = new TaskCompletionSource();
        var running = viewModel.ExecuteTask(new IdleTask(_core), _ => release.Task);
        try
        {
            Assert.Equal(MainViewModel.UpdateStatus.BLOCK, viewModel.Status); // «Подождите…»
            // событие опроса (связь вернулась, статусы перезапрошены) — прежде вернуло бы «Установить игру»
            viewModel.RefreshV2Status();
            Assert.Equal(MainViewModel.UpdateStatus.BLOCK, viewModel.Status);
        }
        finally
        {
            release.SetResult();
            await running;
        }

        // задача завершена — кнопка снова предлагает действие
        viewModel.RefreshV2Status();
        Assert.Equal(MainViewModel.UpdateStatus.GAME_INSTALL, viewModel.Status);
    }

    /// <summary>Пустая задача: ходом управляет сам тест через делегат запуска.</summary>
    private sealed class IdleTask(LauncherServices services) : LauncherTask<object?, object?>(services)
    {
        protected override Task<object?> Action(object? parameters, CancellationToken ct) =>
            Task.FromResult<object?>(null);
    }
}
