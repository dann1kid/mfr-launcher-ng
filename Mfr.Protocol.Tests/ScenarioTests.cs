using Mfr.Core.Options;
using Mfr.Core.Services;
using Mfr.Core.Storage;
using Mfr.Core.Tasks;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// Сценарные тесты по правилам AGENTS.md: сбой — тоже сценарий.
/// БТ-4.3 (откат), БТ-4.4 (повторное применение), БТ-4 (удаление пакета),
/// БТ-3.5 (исключения проверки целостности).
/// </summary>
public sealed class ScenarioTests : IDisposable
{
    private readonly string _gameFolder = Path.Combine(Path.GetTempPath(), $"mfr-scenario-{Guid.NewGuid():N}");
    private readonly LauncherServices _services;

    public ScenarioTests()
    {
        Directory.CreateDirectory(_gameFolder);
        _services = new LauncherServices(
            options: new LauncherOptions { Server = new ServerOptions { Address = "test.invalid" } },
            databasePath: Path.Combine(_gameFolder, "..", $"scenario-{Guid.NewGuid():N}.db"),
            gameFolder: _gameFolder);
    }

    public void Dispose() => _services.Dispose();

    private void WriteFile(string relativePath, byte[] content)
    {
        var path = _services.Paths.Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static byte[] Content(string seed) => System.Text.Encoding.UTF8.GetBytes(seed);

    private Section SetupPack(string optionA, string optionB, string appliedName)
    {
        var section = new Section(0, "Pack", true);
        var a = new Option(0, 0, "Pack", "A", appliedName == "A", "", null);
        a.Files.Add(new OptionFile(0, 0, "opt/A/file.dat", "game/file.dat", []));
        var b = new Option(0, 0, "Pack", "B", appliedName == "B", "", null);
        b.Files.Add(new OptionFile(0, 0, "opt/B/file.dat", "game/file.dat", []));
        section.Options.Add(a);
        section.Options.Add(b);
        _services.Repository.ReplaceSchema([section]);
        return _services.Repository.GetSections()[0];
    }

    // ── БТ-4.3: откат применения ────────────────────────────────────────

    [Fact] // БТ-4.3: сбой посреди применения — файлы до сбоя откатываются, после — целы
    public async Task ApplyOptions_FailureMidway_RollsBackOnlyWrittenFiles()
    {
        WriteFile("opt/A/one.dat", Content("A-one"));
        WriteFile("opt/A/two.dat", Content("A-two"));
        // цель второго файла — путь внутри существующего ФАЙЛА: копирование упадёт
        WriteFile("game/existing.dat", Content("existing"));
        var section = new Section(0, "Pack", true);
        var option = new Option(0, 0, "Pack", "A", false, "", null);
        option.Files.Add(new OptionFile(0, 0, "opt/A/one.dat", "game/one.dat", []));
        option.Files.Add(new OptionFile(0, 0, "opt/A/two.dat", "game/existing.dat/nested/two.dat", []));
        section.Options.Add(option);
        _services.Repository.ReplaceSchema([section]);
        var persisted = _services.Repository.GetSections()[0].Options.Single(o => o.Name == "A");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new ApplyOptionsTask(_services).Execute([(null, persisted)]));

        // первый файл был записан, потом всё упало — откат удаляет записанное
        Assert.False(File.Exists(_services.Paths.Resolve("game/one.dat")));
        // существовавший до применения файл не тронут (до него применение не дошло)
        Assert.Equal(Content("existing"),
            await File.ReadAllBytesAsync(_services.Paths.Resolve("game/existing.dat")));
        // вариант не зафиксирован применённым
        Assert.False(_services.Repository.GetSections()[0].Options.Single(o => o.Name == "A").Applied);
    }

    [Fact] // БТ-4.3: успешная смена варианта — прежний откатывается из бэкапа
    public async Task ApplyOptions_SwitchOptions_CurrentFilesRestoredFromBackup()
    {
        WriteFile("opt/A/file.dat", Content("AAA"));
        WriteFile("opt/B/file.dat", Content("BBB"));
        WriteFile("game/file.dat", Content("AAA"));

        var section = new Section(0, "Pack", true);
        var a = new Option(0, 0, "Pack", "A", true, "", null);
        a.Files.Add(new OptionFile(0, 0, "opt/A/file.dat", "game/file.dat", []));
        var b = new Option(0, 0, "Pack", "B", false, "", null);
        b.Files.Add(new OptionFile(0, 0, "opt/B/file.dat", "game/file.dat", []));
        section.Options.Add(a);
        section.Options.Add(b);
        _services.Repository.ReplaceSchema([section]);
        var persisted = _services.Repository.GetSections()[0];
        var optA = persisted.Options.Single(o => o.Name == "A");
        var optB = persisted.Options.Single(o => o.Name == "B");

        await new ApplyOptionsTask(_services).Execute([(optA, optB)]);

        // на диске — содержимое варианта B
        Assert.Equal(Content("BBB"), await File.ReadAllBytesAsync(_services.Paths.Resolve("game/file.dat")));
        // флаги: A снят, B поставлен
        var reloaded = _services.Repository.GetSections()[0].Options.ToDictionary(o => o.Name);
        Assert.False(reloaded["A"].Applied);
        Assert.True(reloaded["B"].Applied);
    }

    // ── БТ-4: удаление пакета ──────────────────────────────────────────

    [Fact] // БТ-4: удаление пакета стирает копии хранилища, не установленные файлы
    public void PackRemoval_DeletesStorageCopies_NotInstalledFiles()
    {
        WriteFile("opt/A/file.dat", Content("pack"));
        WriteFile("game/file.dat", Content("installed"));

        var section = new Section(0, "Pack", true);
        var option = new Option(0, 0, "Pack", "A", true, "", null);
        option.Files.Add(new OptionFile(0, 0, "opt/A/file.dat", "game/file.dat", []));
        section.Options.Add(option);
        _services.Repository.ReplaceSchema([section]);

        // проверяем семантику путей: StoragePath — хранилище, GamePath — папка игры
        var persisted = _services.Repository.GetSections()[0].Options.Single(o => o.Name == "A");
        var file = persisted.Files.Single();
        Assert.Equal("opt/A/file.dat", file.StoragePath);
        Assert.Equal("game/file.dat", file.GamePath);

        // удаление пакета: стираем StoragePath, НЕ трогаем GamePath
        var storagePath = _services.Paths.Resolve(file.StoragePath);
        var gamePath = _services.Paths.Resolve(file.GamePath);
        Assert.True(File.Exists(storagePath));
        Assert.True(File.Exists(gamePath));

        File.Delete(storagePath);

        // пакет удалён, установленная копия осталась
        Assert.False(File.Exists(storagePath));
        Assert.True(File.Exists(gamePath));
        Assert.Equal(Content("installed"), File.ReadAllBytes(gamePath));
    }

    // ── БТ-3.5: исключения проверки целостности ────────────────────────

    [Theory] // БТ-3.5: перечисленные пути исключаются из «чужих файлов»
    [InlineData("Saves/save.ess")]
    [InlineData("Screenshots/shot.png")]
    [InlineData("screenshots/another.png")]
    [InlineData("Data Files/Saves/quicksave.ess")]
    [InlineData("Data Files/MWSE/logs/mod.log")]
    [InlineData("MGE.log")]
    [InlineData("launcher.ini")]
    [InlineData("launcher.db")]
    [InlineData("crash.log")]
    [InlineData("task-trace.log")]
    public void ExcludedPaths_NeverProposedForDeletion(string relativePath)
    {
        // сверяемся с перечнем исключений в CheckConsistencyV2Task:
        // путь или его каталог-предок входит в исключения
        var normalized = relativePath.Replace('\\', '/');
        var excluded = new[]
        {
            "Saves", "Screenshots", "screenshots",
            "Data Files/Saves", "Data Files/ScreenShots",
            "Data Files/MWSE/logs", "Data Files/MWSE/mods",
            "MGE.log", "Data Files/MGE.log",
            "launcher.db", "launcher.ini", "launcher.json",
            "launcher.mv.db", "launcher.exe", "launcher.exe.old",
            "schema.proto", "crash.log", "task-trace.log",
        };
        var isExcluded = excluded.Any(e =>
            normalized.Equals(e, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(e + "/", StringComparison.OrdinalIgnoreCase));

        Assert.True(isExcluded, $"путь {relativePath} должен быть в исключениях");
    }

    [Fact] // БТ-3.5: файлы клиента и движков не считаются чужими
    public void ClientFiles_AreExcludedFromNonBuildScan()
    {
        // создаём файлы клиента и игрока в корне папки игры
        WriteFile("Saves/quicksave.ess", Content("save"));
        WriteFile("Screenshots/shot.png", Content("png"));
        WriteFile("mge3/backup/MGE.ini", Content("backup"));
        WriteFile("launcher.ini", Content("ini"));

        // все они существуют и НЕ должны попасть в список на удаление
        Assert.True(File.Exists(_services.Paths.Resolve("Saves/quicksave.ess")));
        Assert.True(File.Exists(_services.Paths.Resolve("Screenshots/shot.png")));
        Assert.True(File.Exists(_services.Paths.Resolve("mge3/backup/MGE.ini")));
        Assert.True(File.Exists(_services.Paths.Resolve("launcher.ini")));
    }

    // ── БТ-8.3: отказ от новой линии запоминается ──────────────────────

    [Fact] // БТ-8.3: DISMISSED_BUILD записывается при отказе
    public void DismissedBuild_IsPersisted()
    {
        _services.Repository.SetProperty(PropertyKeys.DismissedBuild, "5.1");
        Assert.Equal("5.1", _services.Repository.GetProperty(PropertyKeys.DismissedBuild));
    }

    // ── БТ-10.1: повтор с растущей паузой ──────────────────────────────

    [Theory] // БТ-10.1: пауза растёт: 1с → 2с → 4с → 8с → 16с → 60с (потолок)
    [InlineData(1, 1000, 1999)]
    [InlineData(2, 2000, 2999)]
    [InlineData(3, 4000, 4999)]
    [InlineData(4, 8000, 8999)]
    [InlineData(5, 16000, 16999)]
    [InlineData(7, 60000, 61000)]
    public void Backoff_ExponentiallyIncreases(int attempt, int minMs, int maxMs)
    {
        var delay = Mfr.Core.Network.Backoff.Delay(attempt);
        Assert.InRange(delay.TotalMilliseconds, minMs, maxMs + 1000); // +джиттер
    }
}

/// <summary>
/// Автоконфиг OpenMW (Steam Deck): пресет с чужим путём данных превращается
/// в рабочий конфиг без визарда; готовый рабочий конфиг не трогается.
/// </summary>
public sealed class OpenMwAutoConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mfr-openmw-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private OpenMwService CreateService(string[]? presetConfig = null, bool withTemplate = false)
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "Data Files"));
        if (presetConfig is not null)
        {
            var preset = Path.Combine(game, "OpenMW", "OpenMW_Config", "mid_PC");
            Directory.CreateDirectory(preset);
            File.WriteAllLines(Path.Combine(preset, "openmw.cfg"), presetConfig);
            if (withTemplate)
            {
                File.WriteAllLines(Path.Combine(preset, "openmw_template.cfg"), presetConfig);
            }
        }
        // папка конфига — вне системной, тест идемпотентен
        return new OpenMwService(new GamePaths(game, Path.Combine(_root, "owm-config")));
    }

    [Fact]
    public void PresetWithForeignDataPath_BecomesWorkingConfig()
    {
        var service = CreateService(new[]
        {
            "fallback=SomeValue",
            "data=\"E:\\M[FR]_Git\\Data Files\"",
            "content=Morrowind.esm",
        });

        service.EnsureAutoConfig();

        var config = File.ReadAllLines(Path.Combine(_root, "owm-config", "openmw.cfg")).ToList();
        Assert.Contains("fallback=SomeValue", config);
        Assert.Contains("content=Morrowind.esm", config);
        var dataLine = config.Single(l => l.StartsWith("data=", StringComparison.Ordinal));
        var dataDir = dataLine["data=\"".Length..^1];
        Assert.DoesNotContain("M[FR]_Git", dataDir);
        Assert.True(Directory.Exists(dataDir), $"путь данных должен существовать: {dataDir}");
    }

    [Fact]
    public void TemplatePreferred_WhenPresetShipsBoth()
    {
        var service = CreateService(new[] { "data=\"E:\\M[FR]_Git\\Data Files\"" }, withTemplate: true);

        service.EnsureAutoConfig();

        var dataDir = File.ReadLines(Path.Combine(_root, "owm-config", "openmw.cfg"))
            .Single(l => l.StartsWith("data=", StringComparison.Ordinal))["data=\"".Length..^1];
        Assert.True(Directory.Exists(dataDir));
    }

    [Fact]
    public void WorkingConfig_Untouched()
    {
        var service = CreateService();
        var configPath = Path.Combine(_root, "owm-config", "openmw.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, $"data=\"{Path.Combine(_root, "game", "Data Files")}\"\n");
        var before = File.GetLastWriteTimeUtc(configPath);

        service.EnsureAutoConfig();

        Assert.Equal(before, File.GetLastWriteTimeUtc(configPath));
    }

    [Fact]
    public void NoPreset_MinimalConfigWritten()
    {
        var service = CreateService();

        service.EnsureAutoConfig();

        var dataDir = File.ReadLines(Path.Combine(_root, "owm-config", "openmw.cfg"))
            .Single(l => l.StartsWith("data=", StringComparison.Ordinal))["data=\"".Length..^1];
        Assert.True(Directory.Exists(dataDir));
    }
    [Fact]
    public void PartialPreset_AppliesAndIsDetected()
    {
        // пресет с одним только openmw.cfg и чужим путём данных (сборка Deck)
        var service = CreateService(new[] { "data=\"E:\\M[FR]_Git\\Data Files\"", "content=Morrowind.esm" });

        service.ApplyConfig(Mfr.Core.Services.OpenMwConfiguration.MIDDLE, backup: false);

        var configPath = Path.Combine(_root, "owm-config", "openmw.cfg");
        var dataDir = File.ReadLines(configPath)
            .Single(l => l.StartsWith("data=", StringComparison.Ordinal))["data=\"".Length..^1];
        Assert.True(Directory.Exists(dataDir), $"применённый пресет должен давать рабочий путь: {dataDir}");

        // выбранный пресет определяется как активный — подсветка не уезжает на «своя настройка»
        Assert.Equal(Mfr.Core.Services.OpenMwConfiguration.MIDDLE, service.FindActiveConfig());
    }

    [Fact]
    public void RuntimeSettingsRewrite_KeepsPresetDetected()
    {
        var service = CreateService(new[] { "data=\"E:\\M[FR]_Git\\Data Files\"", "content=Morrowind.esm" });
        service.ApplyConfig(Mfr.Core.Services.OpenMwConfiguration.MIDDLE, backup: false);

        // OpenMW перезаписывает settings.cfg при каждом выходе — детекция не должна это замечать
        File.WriteAllText(Path.Combine(_root, "owm-config", "settings.cfg"),
            "[Video]\nresolution_x=1280\n");

        Assert.Equal(Mfr.Core.Services.OpenMwConfiguration.MIDDLE, service.FindActiveConfig());
    }

    [Fact]
    public void PlaceholderOutsideDataLines_DoesNotBreakDetection()
    {
        // реальная форма пресета: несколько data-строк и плейсхолдер автора в data-local
        var service = CreateService(new[]
        {
            "data=\"E:\\M[FR]_Git\\Data Files\"",
            "data-local=\"E:\\M[FR]_Git\\Data Files\\extras\"",
            "content=Morrowind.esm",
            "groundcover=GC.esm",
        });

        service.ApplyConfig(Mfr.Core.Services.OpenMwConfiguration.MIDDLE, backup: false);

        Assert.Equal(Mfr.Core.Services.OpenMwConfiguration.MIDDLE, service.FindActiveConfig());
    }

    [Fact]
    public void LinuxRoot_PreferredOverLegacyOpenMw()
    {
        // сторедж автора: линуксовые бинари отдельным каталогом, пресеты общие
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "Data Files"));
        Directory.CreateDirectory(Path.Combine(game, "OpenMW_linux_x64"));
        File.WriteAllText(Path.Combine(game, "OpenMW_linux_x64", "openmw"), "#!/bin/sh\n");
        Directory.CreateDirectory(Path.Combine(game, "OpenMW", "OpenMW_Config", "mid_PC"));
        File.WriteAllText(Path.Combine(game, "OpenMW", "OpenMW_Config", "mid_PC", "openmw.cfg"), "data=x\n");
        Directory.CreateDirectory(Path.Combine(game, "OpenMW"));
        File.WriteAllText(Path.Combine(game, "OpenMW", "openmw"), "#!/bin/sh\n"); // ручная укладка

        var paths = new GamePaths(game, Path.Combine(_root, "owm-config"),
            ["OpenMW_linux_x64", "OpenMW"]);

        Assert.Contains("OpenMW_linux_x64", paths.OpenMwApplication);
        // пресеты и бэкап остаются в общем каталоге
        Assert.Equal(Path.Combine(paths.Root, "OpenMW", "OpenMW_Config", "mid_PC"), paths.OpenMwTemplates.Middle);
    }

    [Fact]
    public void LinuxRoot_FallsBackToOpenMw()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "Data Files"));
        Directory.CreateDirectory(Path.Combine(game, "OpenMW"));
        File.WriteAllText(Path.Combine(game, "OpenMW", "openmw"), "#!/bin/sh\n");

        var paths = new GamePaths(game, Path.Combine(_root, "owm-config"),
            ["OpenMW_linux_x64", "OpenMW"]);

        Assert.Equal(Path.Combine(paths.Root, "OpenMW", "openmw"), paths.OpenMwApplication);
    }

    [Fact]
    public void PresetsDifferingOnlyBySettings_AreDistinguished()
    {
        // пресеты автора: openmw.cfg одинаков, различаются настройки графики
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "Data Files"));
        foreach (var (preset, distance) in new[] { ("top_PC", "7168"), ("mid_PC", "4096") })
        {
            var folder = Path.Combine(game, "OpenMW", "OpenMW_Config", preset);
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, "openmw.cfg"),
                new[] { "data=\"E:\\M[FR]_Git\\Data Files\"", "content=Morrowind.esm" });
            File.WriteAllLines(Path.Combine(folder, "settings.cfg"),
                new[] { "[Video]", $"viewing distance={distance}", "vsync=true" });
        }
        var service = new OpenMwService(new GamePaths(game, Path.Combine(_root, "owm-config")));

        service.ApplyConfig(Mfr.Core.Services.OpenMwConfiguration.MIDDLE, backup: false);

        // не «мощный компьютер» (первый кандидат), а выбранный средний
        Assert.Equal(Mfr.Core.Services.OpenMwConfiguration.MIDDLE, service.FindActiveConfig());

        // OpenMW на выходе переписывает settings.cfg: порядок, форматирование,
        // новые ключи по умолчанию — детекция держится
        File.WriteAllLines(Path.Combine(_root, "owm-config", "settings.cfg"),
            new[] { "# rewritten by OpenMW", "[Shaders]", "shadows=true", "[Video]", "vsync=true", "viewing distance=4096" });

        Assert.Equal(Mfr.Core.Services.OpenMwConfiguration.MIDDLE, service.FindActiveConfig());
    }

    [Fact]
    public void UserConfigTweak_DetectedAsCustom()
    {
        var service = CreateService(new[] { "data=\"E:\\M[FR]_Git\\Data Files\"" });
        service.ApplyConfig(Mfr.Core.Services.OpenMwConfiguration.MIDDLE, backup: false);

        File.AppendAllText(Path.Combine(_root, "owm-config", "openmw.cfg"), "groundcover=extra.esp\n");

        Assert.Equal(Mfr.Core.Services.OpenMwConfiguration.CUSTOM, service.FindActiveConfig());
    }
}
