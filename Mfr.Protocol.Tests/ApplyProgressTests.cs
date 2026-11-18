using Mfr.Core.Storage;
using Mfr.Core.Tasks;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// БТ-1/прогресс: полоса применения пакета — доля записанного объёма,
/// а не числа файлов: тысяча мелких файлов не прыгает процентами на одном крупном.
/// </summary>
public sealed class ApplyProgressTests : IDisposable
{
    private readonly string _gameFolder = Path.Combine(Path.GetTempPath(), $"mfr-applypr-{Guid.NewGuid():N}");
    private readonly LauncherServices _core;

    public ApplyProgressTests()
    {
        Directory.CreateDirectory(_gameFolder);
        _core = new LauncherServices(
            options: new Mfr.Core.Options.LauncherOptions
            {
                Server = new Mfr.Core.Options.ServerOptions { Address = "test.invalid" },
            },
            databasePath: Path.Combine(_gameFolder, "..", $"applypr-{Guid.NewGuid():N}.db"),
            gameFolder: _gameFolder,
            iniPath: Path.Combine(_gameFolder, "..", $"applypr-{Guid.NewGuid():N}.ini"));
    }

    public void Dispose() => _core.Dispose();

    private static OptionFile Storage(int id, string storage, string game, byte[] content)
    {
        System.IO.File.WriteAllBytes(storage, content);
        return new OptionFile(id, id, storage, game, []);
    }

    [Fact] // БТ-1/прогресс: после мелкого файла из пары 1/3 МБ полоса на четверти, не на половине
    public async Task Apply_ProgressFollowsBytes_NotFileCount()
    {
        var option = new Option(1, 1, "S", "HD", false, "", null);
        option.Files.Add(Storage(1,
            Path.Combine(_gameFolder, "small.src"), Path.Combine(_gameFolder, "small.dat"),
            new byte[1024 * 1024]));
        option.Files.Add(Storage(2,
            Path.Combine(_gameFolder, "big.src"), Path.Combine(_gameFolder, "big.dat"),
            new byte[3 * 1024 * 1024]));

        var task = new ApplyOptionsTask(_core);
        var percents = new List<int>();
        task.ProgressChanged += p => percents.Add(p);

        await task.Execute([(null, option)]);

        Assert.Equal(100, percents[^1]);
        // первый файл — четверть объёма: по числу файлов здесь было бы 50
        Assert.Contains(25, percents);
        Assert.DoesNotContain(50, percents);
    }
}
