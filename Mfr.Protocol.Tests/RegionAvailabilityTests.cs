using System.Net;
using Mfr.Core.Network;
using Mfr.Core.Options;
using Mfr.Core.Storage;
using Mfr.Core.Tasks;
using Mfr.Core.V2;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// БТ-10.6: доступность регионов и переключатель. Сохранённый регион, ставший
/// недоступным при живом втором, заменяется доступным — клиент не долбится
/// в мёртвый хост вечно; оба мертвы — остаёмся на сохранённом.
/// </summary>
public sealed class RegionAvailabilityTests : IDisposable
{
    private readonly string _gameFolder = Path.Combine(Path.GetTempPath(), $"mfr-region-{Guid.NewGuid():N}");
    private readonly LauncherServices _core;

    public RegionAvailabilityTests()
    {
        Directory.CreateDirectory(_gameFolder);
        _core = new LauncherServices(
            options: new LauncherOptions { Server = new ServerOptions { Address = "test.invalid" } },
            databasePath: Path.Combine(_gameFolder, "..", $"region-{Guid.NewGuid():N}.db"),
            gameFolder: _gameFolder,
            iniPath: Path.Combine(_gameFolder, "..", $"region-{Guid.NewGuid():N}.ini"));
    }

    public void Dispose() => _core.Dispose();

    /// <summary>проб региона: список каналов отвечает — жив, иначе мёртв
    /// (доступность по /ping при лежащем API — не доступность, БТ-10.6).</summary>
    private sealed class ProbeServer(string aliveHost) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(
                request.RequestUri!.Host == aliveHost ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
    }

    private LauncherServicesV2 V2(HttpMessageHandler handler) => new(_core,
        new ApiClientV2(new HttpClient(handler), "ru.test", "eu.test"), new HttpFileDownloader(new HttpClient(handler)));

    [Fact] // БТ-10.6: сохранённый RU мёртв, EU жив — клиент уходит на EU и сохраняет его
    public async Task SavedRegionDead_OtherAlive_SwitchesToAlive()
    {
        _core.Repository.SetProperty(PropertyKeys.Location, "RU");
        var v2 = V2(new ProbeServer("eu.test"));

        var region = await V2LifecycleService.ResolveRegionAsync(v2, CancellationToken.None);

        Assert.Equal(Mfr.Protocol.Dto.Region.EU, region);
        Assert.Equal("EU", _core.Repository.GetProperty(PropertyKeys.Location));
    }

    [Fact] // БТ-10.6: оба мертвы — остаёмся на сохранённом, без подмены
    public async Task BothDead_StaysOnSaved()
    {
        _core.Repository.SetProperty(PropertyKeys.Location, "RU");
        var v2 = V2(new ProbeServer("nowhere.test"));

        var region = await V2LifecycleService.ResolveRegionAsync(v2, CancellationToken.None);

        Assert.Equal(Mfr.Protocol.Dto.Region.RU, region);
        Assert.Equal("RU", _core.Repository.GetProperty(PropertyKeys.Location));
    }

    [Fact] // БТ-10.6: сохранённый жив — выбор игрока не трогаем
    public async Task SavedRegionAlive_StaysSaved()
    {
        _core.Repository.SetProperty(PropertyKeys.Location, "EU");
        var v2 = V2(new ProbeServer(aliveHost: "eu.test"));

        var region = await V2LifecycleService.ResolveRegionAsync(v2, CancellationToken.None);

        Assert.Equal(Mfr.Protocol.Dto.Region.EU, region);
        Assert.Equal("EU", _core.Repository.GetProperty(PropertyKeys.Location));
    }
}
