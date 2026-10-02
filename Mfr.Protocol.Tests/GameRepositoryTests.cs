using System.Text.Json;
using Mfr.Core.Storage;
using Mfr.Protocol;
using Mfr.Protocol.FileExchange;
using Xunit;

namespace Mfr.Protocol.Tests;

public sealed class GameRepositoryTests : IDisposable
{
    private readonly GameRepository _repository = new(Path.Combine(Path.GetTempPath(), $"mfr-test-{Guid.NewGuid():N}.db"));

    [Fact]
    public void SchemaFile_ParsesConfiguratorFormat()
    {
        const string json =
            """
            {
              "packages": [
                { "name": "Graphics", "options": [
                  { "name": "Distant Land", "description": "Далёкие земли", "image": "img/dl.png", "items": [
                    { "storagePath": "Optional\\DL\\on", "gamePath": "Data Files", "md5": "AQIDBAU=" }
                  ]}
                ]}
              ],
              "extra": [
                { "name": "Music", "items": [ { "path": "Data Files\\music.mp3", "md5": "BgUEAwI=" } ] }
              ]
            }
            """;
        var schema = JsonSerializer.Deserialize<SchemaFile>(json, ProtocolJson.Options);

        Assert.NotNull(schema);
        var package = Assert.Single(schema.Packages);
        Assert.Equal("Graphics", package.Name);
        var option = Assert.Single(package.Options);
        Assert.Equal("Distant Land", option.Name);
        Assert.Equal("Далёкие земли", option.Description);
        Assert.Equal("img/dl.png", option.Image);
        var item = Assert.Single(option.Items);
        Assert.Equal(@"Optional\DL\on", item.StoragePath);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, item.Md5);

        var extra = Assert.Single(schema.Extra);
        Assert.Equal("Music", extra.Name);
        Assert.Equal(new byte[] { 6, 5, 4, 3, 2 }, Assert.Single(extra.Items).Md5);
    }

    [Fact]
    public void ReplaceSchema_ThenGetSections_RoundTrips()
    {
        var section = new Section(0, "Graphics", Downloaded: false);
        var option = new Option(0, 0, "Graphics", "Distant Land", Applied: false, "Далёкие земли", "img/dl.png");
        option.Files.Add(new OptionFile(0, 0, @"Optional\DL\on", "Data Files", [1, 2, 3]));
        section.Options.Add(option);
        var extra = new Extra(0, "Music", Downloaded: false);
        extra.Files.Add(new ExtraFile(0, 0, @"Data Files\music.mp3", [4, 5, 6]));

        _repository.ReplaceSchema([section]);
        _repository.ReplaceExtras([extra]);

        var sections = _repository.GetSections();
        var loadedSection = Assert.Single(sections);
        Assert.Equal("Graphics", loadedSection.Name);
        var loadedOption = Assert.Single(loadedSection.Options);
        Assert.Equal("Graphics", loadedOption.SectionName);
        Assert.Equal("Distant Land", loadedOption.Name);
        Assert.Equal("Далёкие земли", loadedOption.Description);
        Assert.Equal("img/dl.png", loadedOption.ImagePath);
        var loadedFile = Assert.Single(loadedOption.Files);
        Assert.Equal(@"Optional\DL\on", loadedFile.StoragePath);
        Assert.Equal(new byte[] { 1, 2, 3 }, loadedFile.Md5);

        var loadedExtra = Assert.Single(_repository.GetExtras());
        Assert.Equal("Music", loadedExtra.Name);
        Assert.Single(loadedExtra.Files);

        var reloadedOption = _repository.GetSections()[0].Options[0];
        _repository.SetOptionApplied(reloadedOption.Id, true);
        _repository.SetSectionDownloaded(loadedSection.Id, true);
        _repository.SetExtraDownloaded(loadedExtra.Id, true);
        Assert.True(_repository.GetSections()[0].Options[0].Applied);
        Assert.True(_repository.GetSections()[0].Downloaded);
        Assert.True(_repository.GetExtras()[0].Downloaded);
    }

    [Fact]
    public void ReplaceSchemaTwice_LeavesNoOrphans()
    {
        var section = new Section(0, "A", false);
        section.Options.Add(new Option(0, 0, "A", "opt", false, "", null));
        _repository.ReplaceSchema([section]);
        _repository.ReplaceSchema([]);
        Assert.Empty(_repository.GetSections());
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        Assert.Null(_repository.GetProperty(PropertyKeys.GameInstalled));
        _repository.SetProperty(PropertyKeys.GameInstalled, "true");
        Assert.Equal("true", _repository.GetProperty(PropertyKeys.GameInstalled));
        _repository.SetProperty(PropertyKeys.GameInstalled, "false");
        Assert.Equal("false", _repository.GetProperty(PropertyKeys.GameInstalled));
    }

    public void Dispose() => _repository.Dispose();
}
