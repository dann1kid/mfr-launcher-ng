using Mfr.Core.Options;
using Mfr.Core.Storage;
using Mfr.Core.Tasks;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// БТ-10/offline: в офлайне сетевых обращений нет. Удаление пакета — локальное
/// действие и офлайн работает без походов в сеть (прежде пересборка схемы после
/// удаления лезла за манифестом и падала необработанным исключением).
/// </summary>
public sealed class OfflineOptionsTests : IDisposable
{
    private readonly string _gameFolder = Path.Combine(Path.GetTempPath(), $"mfr-offopt-{Guid.NewGuid():N}");
    private readonly LauncherServices _core;

    public OfflineOptionsTests()
    {
        Directory.CreateDirectory(_gameFolder);
        _core = new LauncherServices(
            options: new LauncherOptions { Server = new ServerOptions { Address = "test.invalid" } },
            databasePath: Path.Combine(_gameFolder, "..", $"offopt-{Guid.NewGuid():N}.db"),
            gameFolder: _gameFolder,
            iniPath: Path.Combine(_gameFolder, "..", $"offopt-{Guid.NewGuid():N}.ini"));
        // скачанный пакет: секция в базе + файлы в хранилище
        var section = new Section(1, "Voices", true);
        var option = new Option(1, 1, "Voices", "HD", true, "", null);
        option.Files.Add(new OptionFile(1, 1, "opt/Voices/HD/voice.lip", "Data Files/voice/voice.lip", []));
        section.Options.Add(option);
        _core.Repository.ReplaceSchema([section]);
        var storage = _core.Paths.Resolve("opt/Voices/HD/voice.lip");
        Directory.CreateDirectory(Path.GetDirectoryName(storage)!);
        File.WriteAllText(storage, "voice");
    }

    public void Dispose() => _core.Dispose();

    [Fact] // БТ-10/offline: удаление пакета в офлайне — локально, без сети и без исключений
    public async Task DeletePack_Offline_CompletesWithoutNetwork()
    {
        using var owner = new Mfr.Launcher.ViewModels.MainViewModel(services: _core);
        owner.SwitchOnlineModeCommand.Execute(null); // уходим в офлайн
        Assert.False(owner.OnlineMode);
        var options = new Mfr.Launcher.ViewModels.GameOptionsViewModel(_core, owner, close: () => { });
        var row = Assert.Single(options.Sections, s => s.Name == "Voices");
        Assert.True(row.Downloaded);

        await options.ToggleContent(row); // прежде: сетевой FillSchemaTask падал необработанным

        Assert.False(row.Downloaded);
        Assert.False(File.Exists(_core.Paths.Resolve("opt/Voices/HD/voice.lip")), "файлы пакета удалены");
    }
}
