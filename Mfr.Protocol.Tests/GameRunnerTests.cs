using System.Diagnostics;
using Mfr.Core.Options;
using Mfr.Core.Platform;
using Mfr.Core.Services;
using Mfr.Core.Tasks;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// Шаг настройщика MGE XE (БТ-1.3) и связка платформенной реализации
/// (правило AGENTS.md «платформенное связывание», БТ-6): запуск через раннер,
/// закрытие через несколько секунд, отсутствие настройщика установку не роняет.
/// </summary>
public sealed class GameRunnerTests : IDisposable
{
    private readonly string _gameFolder = Path.Combine(Path.GetTempPath(), $"mfr-runner-{Guid.NewGuid():N}");
    private readonly LauncherServices _services;

    public GameRunnerTests()
    {
        Directory.CreateDirectory(_gameFolder);
        _services = new LauncherServices(
            options: new LauncherOptions { Server = new ServerOptions { Address = "test.invalid" } },
            databasePath: Path.Combine(_gameFolder, "..", $"runner-{Guid.NewGuid():N}.db"),
            gameFolder: _gameFolder,
            iniPath: Path.Combine(_gameFolder, "..", $"runner-{Guid.NewGuid():N}.ini"));
    }

    public void Dispose() => _services.Dispose();

    /// <summary>
    /// Подменяемый раннер: запоминает запрос и возвращает процесс от своего
    /// дескриптора, а тест держит второй дескриптор того же процесса — метод
    /// распоряжается только своим.
    /// </summary>
    private sealed class ScriptedRunner(Func<Process> start) : IPlatformProcessRunner
    {
        public string? RequestedPath { get; private set; }
        public string? RequestedWorkDir { get; private set; }
        public Process? Witness { get; private set; }

        public Process StartApplication(string executablePath, string workingDirectory) =>
            throw new NotSupportedException();

        public Process StartElevated(string executablePath, string workingDirectory) =>
            throw new NotSupportedException();

        public Process StartManaged(string executablePath, string workingDirectory)
        {
            RequestedPath = executablePath;
            RequestedWorkDir = workingDirectory;
            var started = start();
            Witness = Process.GetProcessById(started.Id);
            return started;
        }
    }

    private static Process StartSleepingProcess()
    {
        // ping — переносимый «сон» на Windows, sleep — на остальных ОС
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 30 127.0.0.1")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        return Process.Start(psi) ?? throw new InvalidOperationException("тестовый процесс не запустился");
    }

    [Fact] // БТ-1.3: живой настройщик закрывается клиентом по истечении паузы
    public async Task MgeConfigurator_AliveProcess_IsKilledAfterWait()
    {
        var runner = new ScriptedRunner(StartSleepingProcess);
        var gameRunner = new GameRunner(_services.Paths, runner);
        try
        {
            await gameRunner.RunMgeConfiguratorAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None);
        }
        finally
        {
            // процесс сна-30 не мог выйти сам за 150 мс: закрыл его метод
            Assert.True(runner.Witness!.WaitForExit(5000), "настройщик должен быть закрыт клиентом");
        }
        Assert.Equal(_services.Paths.MgeApplication, runner.RequestedPath);
        Assert.Equal(_services.Paths.Root, runner.RequestedWorkDir);
    }

    [Fact] // БТ-1.3: настройщика нет — запуск не роняет установку
    public async Task MgeConfigurator_MissingConfigurator_ReturnsWithoutError()
    {
        // отсутствие файла проявляется исключением платформенного запуска
        var runner = new ScriptedRunner(() => throw new System.ComponentModel.Win32Exception(2, "нет файла"));
        var gameRunner = new GameRunner(_services.Paths, runner);

        await gameRunner.RunMgeConfiguratorAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Equal(_services.Paths.MgeApplication, runner.RequestedPath);
    }
}

/// <summary>Связка платформы: в бой идёт реализация текущей ОС, не заглушка.</summary>
public sealed class PlatformWiringTests
{
    [Fact] // БТ-6/«платформенное связывание»: настоящая реализация, не NullProcessRunner
    public void Launcher_BindsRealProcessRunnerForCurrentOs()
    {
        var runner = Mfr.Launcher.PlatformServices.ProcessRunner;

        Assert.IsNotType<NullProcessRunner>(runner);
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<Mfr.Platform.Windows.WindowsProcessRunner>(runner);
        }
        else
        {
            Assert.IsType<Mfr.Platform.Linux.LinuxProcessRunner>(runner);
        }
    }
}
