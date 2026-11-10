using System.Text.Json.Serialization;
using System.Reflection;
using System.Text.RegularExpressions;
using Mfr.Protocol.Dto;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// Договорный тест (правило 1 AGENTS): DTO и пути клиента сверяются с
/// contract/client-api.yaml — пути, параметры, обязательность и имена полей.
/// Контракт заморожен: расхождение клиента с yaml — ошибка клиента.
/// </summary>
public class ContractTests
{
    private static readonly Lazy<ContractYaml> Parsed = new(() => new ContractYaml(
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(ContractTests).Assembly.Location)!,
            "..", "..", "..", "..", "contract", "client-api.yaml")))
            .Replace("\r\n", "\n")));

    private sealed class ContractYaml(string yaml)
    {
        private readonly string _yaml = yaml;

        public HashSet<string> Paths() =>
            [.. Regex.Matches(_yaml, @"^  (?:""(/[^""]+)""|(/[^:\s]+)):", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value + m.Groups[2].Value)];

        public List<string> Parameters(string path)
        {
            var section = PathSection(path);
            return [.. Regex.Matches(section, @"- name: (\S+)", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)];
        }

        public bool IsRequired(string schema, string property)
        {
            var section = SchemaSection(schema);
            var required = Regex.Match(section, @"required:\n((?:        - \w+\n)+)");
            return required.Success &&
                   required.Groups[1].Value.Split('\n').Any(l => l.Trim() == $"- {property}");
        }

        public bool HasProperty(string schema, string property) =>
            SchemaSection(schema).Contains($"  {property}:", StringComparison.Ordinal);

        private string PathSection(string path)
        {
            var start = IndexOfPath(path);
            Assert.True(start >= 0, $"путь {path} не найден в контракте");
            var rest = _yaml[start..];
            var next = Regex.Match(rest[6..], @"^  (?:""/|/)", RegexOptions.Multiline);
            return next.Success ? rest[..(next.Index + 6)] : rest;
        }

        private string SchemaSection(string schema)
        {
            var marker = $"    {schema}:";
            var start = _yaml.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0, $"схема {schema} не найдена в контракте");
            var rest = _yaml[start..];
            var next = Regex.Match(rest[marker.Length..], @"^    \w+:", RegexOptions.Multiline);
            return next.Success ? rest[..(next.Index + marker.Length)] : rest;
        }

        private int IndexOfPath(string path) =>
            _yaml.IndexOf($"  \"{path}\":", StringComparison.Ordinal) is var quoted && quoted >= 0
                ? quoted
                : _yaml.IndexOf($"  {path}:", StringComparison.Ordinal);
    }

    // ── Пути и параметры ──────────────────────────────────────────────────

    [Theory] // каждый путь, который вызывает ApiClientV2, объявлен в контракте
    [InlineData("/ping")]
    [InlineData("/v2/game/{channel}/version")]
    [InlineData("/v2/game/channels")]
    [InlineData("/v1/game/files")]
    [InlineData("/v2/launcher/version")]
    [InlineData("/v2/launcher/files")]
    public void Paths_ClientCalls_AreDeclaredInContract(string path) =>
        Assert.Contains(path, Parsed.Value.Paths());

    [Fact] // контракт не растёт мимо ведома клиента: новый путь роняет тест
    public void Paths_ContractHasNoUnknownPaths()
    {
        var known = new HashSet<string>
        {
            "/ping", "/v1/game/version", "/v2/game/{channel}/version", "/v2/game/channels",
            "/v1/game/files", "/v1/launcher/version", "/v2/launcher/version",
            "/v1/launcher/files", "/v2/launcher/files",
        };
        var inContract = Parsed.Value.Paths();
        Assert.True(known.IsSupersetOf(inContract),
            $"в контракте новые пути: {string.Join(", ", inContract.Except(known))}");
    }

    [Theory] // параметры запроса объявлены в контракте для этих путей
    [InlineData("/v2/game/channels", "os")]
    [InlineData("/v2/game/channels", "X-Client-ID")]
    [InlineData("/v2/game/{channel}/version", "os")]
    [InlineData("/v2/game/{channel}/version", "X-Client-ID")]
    [InlineData("/v1/game/files", "os")]
    [InlineData("/v1/game/files", "version")]
    [InlineData("/v1/game/files", "region")]
    [InlineData("/v2/launcher/version", "os")]
    [InlineData("/v2/launcher/files", "os")]
    [InlineData("/v2/launcher/files", "version")]
    [InlineData("/v2/launcher/files", "region")]
    public void Parameters_DeclaredInContract(string path, string parameter) =>
        Assert.Contains(parameter, Parsed.Value.Parameters(path));

    // ── DTO: обязательность и имена полей ────────────────────────────────

    [Fact] // Version: id обязателен
    public void DtoVersion_MatchesContract()
    {
        Assert.True(Parsed.Value.IsRequired("Version", "id"));
        Assert.Equal("id", JsonNameOf(typeof(VersionId), nameof(VersionId.Id)));
    }

    [Fact] // ChannelList: channels обязателен, элемент — объект с обязательным id
    public void DtoChannelList_MatchesContract()
    {
        Assert.True(Parsed.Value.IsRequired("ChannelList", "channels"));
        Assert.Equal("channels", JsonNameOf(typeof(ChannelList), nameof(ChannelList.Channels)));
        Assert.True(Parsed.Value.IsRequired("Channel", "id"));
        Assert.Equal("id", JsonNameOf(typeof(ChannelDto), nameof(ChannelDto.Id)));
    }

    [Fact] // GameSchemaResponse: host/files/schema обязательны, сжатые копии — нет
    public void DtoGameSchema_MatchesContract()
    {
        var parsed = Parsed.Value;
        Assert.True(parsed.IsRequired("GameSchemaResponse", "host"));
        Assert.True(parsed.IsRequired("GameSchemaResponse", "files"));
        Assert.True(parsed.IsRequired("GameSchemaResponse", "schema"));
        Assert.False(parsed.IsRequired("GameSchemaResponse", "compressed_files"));
        Assert.False(parsed.IsRequired("GameSchemaResponse", "compressed_schema"));
        Assert.True(parsed.HasProperty("GameSchemaResponse", "compressed_files"));
        Assert.True(parsed.HasProperty("GameSchemaResponse", "compressed_schema"));

        Assert.Equal("host", JsonNameOf(typeof(GameSchemaResponse), nameof(GameSchemaResponse.Host)));
        Assert.Equal("files", JsonNameOf(typeof(GameSchemaResponse), nameof(GameSchemaResponse.Files)));
        Assert.Equal("schema", JsonNameOf(typeof(GameSchemaResponse), nameof(GameSchemaResponse.Schema)));
        Assert.Equal("compressed_files", JsonNameOf(typeof(GameSchemaResponse), nameof(GameSchemaResponse.CompressedFiles)));
        Assert.Equal("compressed_schema", JsonNameOf(typeof(GameSchemaResponse), nameof(GameSchemaResponse.CompressedSchema)));
    }

    [Fact] // LauncherSchemaResponse: все четыре поля обязательны
    public void DtoLauncherSchema_MatchesContract()
    {
        var parsed = Parsed.Value;
        Assert.True(parsed.IsRequired("LauncherSchemaResponse", "host"));
        Assert.True(parsed.IsRequired("LauncherSchemaResponse", "launcher_storage"));
        Assert.True(parsed.IsRequired("LauncherSchemaResponse", "jdk"));
        Assert.True(parsed.IsRequired("LauncherSchemaResponse", "jdk_storage"));

        Assert.Equal("host", JsonNameOf(typeof(LauncherSchemaResponse), nameof(LauncherSchemaResponse.Host)));
        Assert.Equal("launcher_storage", JsonNameOf(typeof(LauncherSchemaResponse), nameof(LauncherSchemaResponse.LauncherStorage)));
        Assert.Equal("jdk", JsonNameOf(typeof(LauncherSchemaResponse), nameof(LauncherSchemaResponse.Jdk)));
        Assert.Equal("jdk_storage", JsonNameOf(typeof(LauncherSchemaResponse), nameof(LauncherSchemaResponse.JdkStorage)));
    }

    private static string? JsonNameOf(Type dto, string property) =>
        dto.GetProperty(property)?.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
}
