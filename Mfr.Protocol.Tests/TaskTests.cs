using System.Text.Json;
using Mfr.Core.Exceptions;
using Mfr.Core.Storage;
using Mfr.Core.Tasks;
using Mfr.Protocol;
using Mfr.Protocol.Cryptography;
using Mfr.Protocol.FileExchange;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>Offline tests of the schema and options tasks on a temporary game folder.</summary>
public sealed class TaskTests : IDisposable
{
    private readonly string _gameFolder = Path.Combine(Path.GetTempPath(), $"mfr-task-{Guid.NewGuid():N}");
    private readonly LauncherServices _services;

    public TaskTests()
    {
        Directory.CreateDirectory(_gameFolder);
        _services = new LauncherServices(
            databasePath: Path.Combine(_gameFolder, "..", $"test-{Guid.NewGuid():N}.db"),
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

    [Fact]
    public async Task FillSchema_AutoDetectsAppliedOptionByMd5()
    {
        WriteFile(@"opt\A\file.dat", Content("option A"));
        WriteFile(@"opt\A\extra.dat", Content("option A extra"));
        WriteFile(@"game\file.dat", Content("option B"));

        var schema = new SchemaFile(
        [
            new SchemaPackage("Pack", [
                new SchemaOption("A", "desc A", null, [
                    new SchemaOptionItem(@"opt\A\file.dat", @"game\file.dat", Md5.Hash(Content("option A"))),
                    new SchemaOptionItem(@"opt\A\extra.dat", @"game\extra.dat", Md5.Hash(Content("option A extra"))),
                ]),
                new SchemaOption("B", "desc B", null, [
                    new SchemaOptionItem(@"opt\B\file.dat", @"game\file.dat", Md5.Hash(Content("option B"))),
                ]),
            ]),
        ], []);
        File.WriteAllText(
            Path.Combine(_gameFolder, "schema.json"),
            JsonSerializer.Serialize(schema, ProtocolJson.Options));

        await new FillSchemaTask(_services).Execute(null);

        var section = Assert.Single(_services.Repository.GetSections());
        Assert.Equal("Pack", section.Name);
        Assert.False(section.Downloaded);
        Assert.False(section.Options.Single(o => o.Name == "A").Applied);
        Assert.True(section.Options.Single(o => o.Name == "B").Applied); // game file matches B
        Assert.NotNull(_services.Repository.GetProperty(PropertyKeys.Schema));
    }

    [Fact]
    public async Task FillSchema_SecondRunWithSameSchema_PreservesFlags()
    {
        var schema = new SchemaFile(
        [
            new SchemaPackage("Pack", [new SchemaOption("opt", "d", null, [])]),
        ], []);
        File.WriteAllText(Path.Combine(_gameFolder, "schema.json"), JsonSerializer.Serialize(schema, ProtocolJson.Options));

        await new FillSchemaTask(_services).Execute(null);
        var section = Assert.Single(_services.Repository.GetSections());
        var optionId = section.Options[0].Id;
        _services.Repository.SetOptionApplied(optionId, true);
        _services.Repository.SetSectionDownloaded(section.Id, true);

        await new FillSchemaTask(_services).Execute(null); // same schema.md5 → no rebuild

        section = Assert.Single(_services.Repository.GetSections());
        Assert.True(section.Downloaded);
        Assert.True(section.Options[0].Applied);
    }

    [Fact]
    public async Task ApplyOptions_SwitchesFilesAndFlags()
    {
        WriteFile(@"opt\A\file.dat", Content("option A"));
        WriteFile(@"opt\B\file.dat", Content("option B"));

        var section = new Section(0, "Pack", true);
        var optionA = new Option(0, 0, "Pack", "A", false, "", null);
        optionA.Files.Add(new OptionFile(0, 0, @"opt\A\file.dat", @"game\file.dat", Md5.Hash(Content("option A"))));
        var optionB = new Option(0, 0, "Pack", "B", false, "", null);
        optionB.Files.Add(new OptionFile(0, 0, @"opt\B\file.dat", @"game\file.dat", Md5.Hash(Content("option B"))));
        section.Options.Add(optionA);
        section.Options.Add(optionB);
        _services.Repository.ReplaceSchema([section]);
        var persisted = _services.Repository.GetSections()[0];
        var a = persisted.Options.Single(o => o.Name == "A");
        var b = persisted.Options.Single(o => o.Name == "B");

        await new ApplyOptionsTask(_services).Execute([(null, a)]);
        Assert.Equal(Content("option A"), await File.ReadAllBytesAsync(_services.Paths.Resolve(@"game\file.dat")));
        Assert.True(_services.Repository.GetSections()[0].Options.Single(o => o.Name == "A").Applied);

        await new ApplyOptionsTask(_services).Execute([(a, b)]);
        Assert.Equal(Content("option B"), await File.ReadAllBytesAsync(_services.Paths.Resolve(@"game\file.dat")));
        var reloaded = _services.Repository.GetSections()[0].Options.ToDictionary(o => o.Name);
        Assert.False(reloaded["A"].Applied);
        Assert.True(reloaded["B"].Applied);
    }

    [Fact]
    public async Task ApplyOptions_MissingStorageFiles_FailsWithReport()
    {
        var section = new Section(0, "Pack", true);
        var option = new Option(0, 0, "Pack", "X", false, "", null);
        option.Files.Add(new OptionFile(0, 0, @"opt\missing.dat", @"game\missing.dat", []));
        section.Options.Add(option);
        _services.Repository.ReplaceSchema([section]);
        var persisted = _services.Repository.GetSections()[0].Options[0];

        var exception = await Assert.ThrowsAsync<TaskExecuteException>(
            () => new ApplyOptionsTask(_services).Execute([(null, persisted)]));
        Assert.Contains("не применены", exception.InnerException?.Message);
    }

    [Fact]
    public async Task ApplyOptions_CopyFailure_RestoresBackup()
    {
        WriteFile(@"opt\A\file.dat", Content("option A"));
        WriteFile(@"game\file.dat", Content("option A"));
        WriteFile(@"opt\C\file.dat", Content("option C"));

        var section = new Section(0, "Pack", true);
        var optionA = new Option(0, 0, "Pack", "A", false, "", null);
        optionA.Files.Add(new OptionFile(0, 0, @"opt\A\file.dat", @"game\file.dat", []));
        var optionC = new Option(0, 0, "Pack", "C", false, "", null);
        // target path nests inside an existing FILE → copy always fails
        optionC.Files.Add(new OptionFile(0, 0, @"opt\C\file.dat", @"opt\A\file.dat\nested\file.dat", []));
        section.Options.Add(optionA);
        section.Options.Add(optionC);
        _services.Repository.ReplaceSchema([section]);
        var persisted = _services.Repository.GetSections()[0];
        var a = persisted.Options.Single(o => o.Name == "A");
        var c = persisted.Options.Single(o => o.Name == "C");

        await Assert.ThrowsAnyAsync<Exception>(() => new ApplyOptionsTask(_services).Execute([(a, c)]));

        // the current option's file is restored from the backup and stays applied-free
        Assert.Equal(Content("option A"), await File.ReadAllBytesAsync(_services.Paths.Resolve(@"game\file.dat")));
        var reloaded = _services.Repository.GetSections()[0].Options.ToDictionary(o => o.Name);
        Assert.False(reloaded["A"].Applied); // nothing persisted: the switch failed
        Assert.False(reloaded["C"].Applied);
    }
}
