using Mfr.Core.Services;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// БТ-5.3: копия «Своих настроек» OpenMW. Детекция пресета сравнивает только
/// ключи пресета — правки игрока в остальных ключах (fov) она не видит,
/// и прежде переход со «штатного» пресета стирал такие настройки без копии.
/// </summary>
public sealed class OpenMwSettingsBackupTests : IDisposable
{
    private readonly string _gameFolder = Path.Combine(Path.GetTempPath(), $"mfr-omwbackup-{Guid.NewGuid():N}");
    private readonly string _configFolder = Path.Combine(Path.GetTempPath(), $"mfr-omwcfg-{Guid.NewGuid():N}");
    private readonly OpenMwService _service;

    public OpenMwSettingsBackupTests()
    {
        Directory.CreateDirectory(_gameFolder);
        // пресет mid выражен настройками; top и low — только openmw.cfg
        Write($"OpenMW/OpenMW_Config/mid_PC/settings.cfg", "[Video]\nresolution=1920x1080\n");
        Write($"OpenMW/OpenMW_Config/mid_PC/openmw.cfg", "encoding=win1251\n");
        Write($"OpenMW/OpenMW_Config/top_PC/openmw.cfg", "encoding=win1251\n");
        // машинные ключи «мощного» пресета — с машины автора, 4K в окне
        Write($"OpenMW/OpenMW_Config/top_PC/settings.cfg",
            "[Video]\nresolution x = 3840\nresolution y = 2160\nwindow mode = 0\nscreen = 0\n");
        Write($"OpenMW/OpenMW_Config/low_PC/openmw.cfg", "encoding=win1252\n");
        _service = new OpenMwService(new GamePaths(_gameFolder, openMwConfigFolder: _configFolder));
    }

    public void Dispose()
    {
        Directory.Delete(_gameFolder, recursive: true);
        if (Directory.Exists(_configFolder))
        {
            Directory.Delete(_configFolder, recursive: true);
        }
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_gameFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string BackupFile(string name) =>
        Path.Combine(_gameFolder, "OpenMW", "OpenMW_Config", "backup", name);

    private void InstallActiveLikeMiddleWithFov(int fov)
    {
        File.WriteAllText(Path.Combine(_configFolder, "settings.cfg"),
            $"[Video]\nresolution=1920x1080\n[Camera]\nfov={fov}\n");
        File.WriteAllText(Path.Combine(_configFolder, "openmw.cfg"), "encoding=win1251\n");
    }

    [Fact] // БТ-5.3: правка вне ключей пресета не видна детекции — копия всё равно делается
    public void SwitchFromDetectedPreset_WithPlayerKeysOutsidePreset_StillFillsBackupSlot()
    {
        InstallActiveLikeMiddleWithFov(90);

        _service.ApplyConfig(OpenMwConfiguration.HIGH, backup: false); // окно сочло конфигурацию пресетом mid

        Assert.True(File.Exists(BackupFile("settings.cfg")), "копия «Своих настроек» обязана появиться");
        Assert.Contains("fov=90", File.ReadAllLines(BackupFile("settings.cfg")));
    }

    [Fact] // БТ-5.3: занятый слот пресетным переходом не перезаписывается
    public void PresetToPresetSwitch_DoesNotOverwriteFilledBackupSlot()
    {
        InstallActiveLikeMiddleWithFov(90);
        _service.ApplyConfig(OpenMwConfiguration.HIGH, backup: false); // слот заполнен настройкой игрока
        // дальше игрок на чистом top: переход дальше не должен затирать слот
        File.WriteAllText(Path.Combine(_configFolder, "settings.cfg"), "[Video]\nresolution=1920x1080\n");

        _service.ApplyConfig(OpenMwConfiguration.LOW, backup: false);

        Assert.Contains("fov=90", File.ReadAllLines(BackupFile("settings.cfg")));
    }

    [Fact] // БТ-5.3: «Свои настройки» возвращают сохранённое, включая ключи вне пресета
    public void RestoreCustom_ReturnsPlayerKeysOutsidePreset()
    {
        InstallActiveLikeMiddleWithFov(90);
        _service.ApplyConfig(OpenMwConfiguration.HIGH, backup: false); // слот сохранён
        File.Delete(Path.Combine(_configFolder, "settings.cfg"));      // пресет top стёр настройки

        _service.ApplyConfig(OpenMwConfiguration.CUSTOM, backup: false);

        Assert.Contains("fov=90", File.ReadAllLines(Path.Combine(_configFolder, "settings.cfg")));
    }

    [Fact] // БТ-5: машинные ключи [Video] игрока пресетом не перетаскиваются с машины автора
    public void ApplyPreset_KeepsPlayerVideoModeAndResolution()
    {
        // игрок: 2560×1440, полноэкранный; шаблон «мощного»: 4K, оконный
        File.WriteAllText(Path.Combine(_configFolder, "settings.cfg"),
            "[Video]\nresolution x = 2560\nresolution y = 1440\nwindow mode = 1\n");

        _service.ApplyConfig(OpenMwConfiguration.HIGH, backup: false);

        var settings = File.ReadAllLines(Path.Combine(_configFolder, "settings.cfg"));
        Assert.Contains("resolution x = 2560", settings);
        Assert.Contains("resolution y = 1440", settings);
        Assert.Contains("window mode = 1", settings);
        Assert.DoesNotContain("resolution x = 3840", settings);
        Assert.DoesNotContain("window mode = 0", settings);
    }

    [Fact] // БТ-5: детекция пресета машинные ключи игнорирует — радио не врёт после применения
    public void Detection_AfterPresetWithPlayerVideo_StillDetectsPreset()
    {
        File.WriteAllText(Path.Combine(_configFolder, "settings.cfg"),
            "[Video]\nresolution x = 2560\nresolution y = 1440\nwindow mode = 1\n");

        _service.ApplyConfig(OpenMwConfiguration.HIGH, backup: false);

        Assert.Equal(OpenMwConfiguration.HIGH, _service.FindActiveConfig());
    }
}
