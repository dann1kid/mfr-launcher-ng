using System.Text;
using Mfr.Core.Options;
using Mfr.Core.Services;
using Mfr.Core.Tasks;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// БТ-5.3: копия «Своих настроек» решается конфигурацией, найденной при открытии
/// окна пресетов, а не последовательностью кликов. И БТ-5: определение пресета
/// не держит дескриптор MGE.ini — применение пресета вслед за ним не падает.
/// </summary>
public sealed class PresetBackupTests : IDisposable
{
    private readonly string _gameFolder = Path.Combine(Path.GetTempPath(), $"mfr-preset-{Guid.NewGuid():N}");
    private readonly LauncherServices _core;

    public PresetBackupTests()
    {
        Directory.CreateDirectory(_gameFolder);
        _core = new LauncherServices(
            options: new LauncherOptions { Server = new ServerOptions { Address = "test.invalid" } },
            databasePath: Path.Combine(_gameFolder, "..", $"preset-{Guid.NewGuid():N}.db"),
            gameFolder: _gameFolder,
            iniPath: Path.Combine(_gameFolder, "..", $"preset-{Guid.NewGuid():N}.ini"));
    }

    public void Dispose() => _core.Dispose();

    private static Mfr.Launcher.Views.PresetWindow.ViewModel NewViewModel(
        MgeConfiguration detected,
        out System.Collections.Generic.List<(MgeConfiguration Config, bool Backup)> applied)
    {
        var log = new System.Collections.Generic.List<(MgeConfiguration, bool)>();
        applied = log;
        var viewModel = new Mfr.Launcher.Views.PresetWindow.ViewModel("Пресеты", mgeMode: true, onExtra: null, close: () => { })
        {
            DetectMge = () => detected,
            ApplyMge = (config, backup) => log.Add((config, backup)),
        };
        viewModel.LoadCurrent();
        return viewModel;
    }

    [Fact] // БТ-5.3(а): открыто на «Своих настройках», два пресета подряд — копия всё равно делается
    public void Backup_DecidedByDetectedCustom_TwoPresetClicksStillBackup()
    {
        var viewModel = NewViewModel(MgeConfiguration.CUSTOM, out var applied);

        viewModel.IsHigh = true; // первый клик по пресету…
        viewModel.IsLow = true;  // …и второй: прежде второй клик стирал решение о копии
        viewModel.ApplyCommand.Execute(null);

        var (config, backup) = Assert.Single(applied);
        Assert.Equal(MgeConfiguration.LOW, config);
        Assert.True(backup); // своя конфигурация на открытии — снимок обязателен
    }

    [Fact] // БТ-5.3(б): открыто на пресете, клик «Свои настройки» и снова пресет — копия не перезаписывается
    public void Backup_DecidedByDetectedCustom_CustomClickDoesNotPoisonBackup()
    {
        var viewModel = NewViewModel(MgeConfiguration.MIDDLE, out var applied);

        viewModel.IsCustom = true; // зашёл в «Свои настройки»…
        viewModel.IsHigh = true;   // …передумал: прежде копия затиралась штатным пресетом
        viewModel.ApplyCommand.Execute(null);

        var (config, backup) = Assert.Single(applied);
        Assert.Equal(MgeConfiguration.HIGH, config);
        Assert.False(backup); // на открытии был пресет — хранимая копия не тронута
    }

    [Fact] // БТ-5.3: возврат «Своих настроек» из окна, открытого на пресете, не копирует пресет в бэкап
    public void RestoreCustom_FromPresetWindow_AppliesWithoutBackup()
    {
        var viewModel = NewViewModel(MgeConfiguration.MIDDLE, out var applied);

        viewModel.IsCustom = true;
        viewModel.ApplyCommand.Execute(null);

        var (config, backup) = Assert.Single(applied);
        Assert.Equal(MgeConfiguration.CUSTOM, config);
        Assert.False(backup);
    }
}
