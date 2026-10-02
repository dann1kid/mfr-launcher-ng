using System.Text.Json;
using Mfr.Protocol;
using Mfr.Protocol.Dto;
using Mfr.Protocol.Enums;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// These tests run against byte-exact samples captured from the production
/// server (https://mfr.fullrest.ru) on 2026-10-02. If they fail after a
/// server-side change, re-capture the files in Golden/ — do not "fix" the DTOs
/// blindly: the old Java clients depend on the same wire format.
/// </summary>
public sealed class RestGoldenTests
{
    private static string Golden(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", fileName));

    [Fact]
    public void Builds_ParseFromProductionSample()
    {
        var builds = JsonSerializer.Deserialize<BuildDto[]>(Golden("api_v1_game.json"), ProtocolJson.Options);

        Assert.NotNull(builds);
        var build = Assert.Single(builds);
        Assert.Equal(1, build.Id);
        Assert.Equal("Релиз", build.Name);
        Assert.Equal(new DateTime(2024, 1, 1, 18, 6, 39), build.LastUpdate);
        Assert.True(build.Default);
    }

    [Fact]
    public void LauncherVersions_ParseFromProductionSample()
    {
        var clients = JsonSerializer.Deserialize<LauncherVersionDto[]>(Golden("api_v1_client.json"), ProtocolJson.Options);

        Assert.NotNull(clients);
        var windows = Assert.Single(clients);
        Assert.Equal(SystemType.WINDOWS, windows.System);
        Assert.Equal("3.2.2", windows.Version);
        Assert.Equal(Convert.FromBase64String("FG9Tw7lKOquu1Qb6Dx8PDg=="), windows.Md5);
        Assert.Equal(16, windows.Md5.Length);
        Assert.Equal(64_853_914L, windows.Size);
    }

    [Fact]
    public void FilteredManifest_IsEmptyCategories()
    {
        var content = JsonSerializer.Deserialize<Content>(Golden("api_v1_game_1_filtered.json"), ProtocolJson.Options);

        Assert.NotNull(content);
        Assert.Empty(content.Categories);
    }

    [Fact]
    public void FullManifest_ParsesAllFiles()
    {
        var content = JsonSerializer.Deserialize<Content>(Golden("api_v1_game_1.json"), ProtocolJson.Options);

        Assert.NotNull(content);
        Assert.Equal(3, content.Categories.Count);

        var byType = content.Categories.ToDictionary(c => c.Type);
        Assert.Equal(new[] { ContentType.MAIN, ContentType.EXTRA, ContentType.OPTIONAL }, byType.Keys.OrderBy(t => t).ToArray());
        Assert.True(byType[ContentType.MAIN].Required);

        var totalFiles = content.Categories.Sum(c => c.Items.Sum(i => i.Files.Count));
        Assert.Equal(69_271, totalFiles);

        var firstFile = byType[ContentType.MAIN].Items[0].Files[0];
        Assert.Equal("Core", byType[ContentType.MAIN].Items[0].Name);
        Assert.Equal(45, firstFile.Id);
        Assert.Equal(@"OpenMW\bsatool.pdb", firstFile.Path);
        Assert.False(firstFile.Active);
        Assert.Empty(firstFile.Md5);
        Assert.Equal(7_335_936L, firstFile.Size);

        var angelIni = content.Categories.SelectMany(c => c.Items).SelectMany(i => i.Files)
            .Single(f => f.Id == 68541);
        Assert.Equal("angel.ini", angelIni.Path);
        Assert.True(angelIni.Active);
        Assert.Equal(16, angelIni.Md5.Length);

        var activeCount = content.Categories.SelectMany(c => c.Items).SelectMany(i => i.Files).Count(f => f.Active);
        Assert.Equal(68_929, activeCount);
    }

    [Fact]
    public void DateConverter_RoundTripsJacksonShapes()
    {
        var read = JsonSerializer.Deserialize<BuildDto[]>(@"[{""id"":2,""name"":""x"",""lastUpdate"":""2024-01-01T18:06"",""default"":false}]", ProtocolJson.Options);
        Assert.Equal(new DateTime(2024, 1, 1, 18, 6, 0), read![0].LastUpdate);

        var written = JsonSerializer.Serialize(new BuildDto(2, "x", new DateTime(2024, 1, 1, 18, 6, 0), false), ProtocolJson.Options);
        Assert.Contains("2024-01-01T18:06", written);
    }
}
