using System.Text.Json.Serialization;

namespace Mfr.Protocol.Dto;

/// <summary>v2 API DTOs of the dev-branch client (3.3.x), field names verbatim.</summary>
public sealed record ChannelList(
    [property: JsonPropertyName("channels")] IReadOnlyList<ChannelDto> Channels);

public sealed record ChannelDto(
    [property: JsonPropertyName("id")] string Id);

/// <summary>`{ "id": "..." }` — active version id of a line / launcher.</summary>
public sealed record VersionId(
    [property: JsonPropertyName("id")] string Id);

/// <summary>`/v1/game/files` — links to the protobuf manifests.</summary>
public sealed record GameSchemaResponse(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("files")] string Files,
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("compressed_files")] string? CompressedFiles,
    [property: JsonPropertyName("compressed_schema")] string? CompressedSchema);

/// <summary>`/v2/launcher/files` — launcher binary + JDK objects.</summary>
public sealed record LauncherSchemaResponse(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("launcher_storage")] string LauncherStorage,
    [property: JsonPropertyName("jdk")] string Jdk,
    [property: JsonPropertyName("jdk_storage")] string JdkStorage);

public enum Region
{
    RU,
    EU,
}
