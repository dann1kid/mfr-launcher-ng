using System.Text.Json.Serialization;
using Mfr.Protocol.Enums;

namespace Mfr.Protocol.Dto;

/// <summary>GET /api/v1/game — one element per build.</summary>
public sealed record BuildDto(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("lastUpdate")] DateTime LastUpdate,
    [property: JsonPropertyName("default")] bool Default);

/// <summary>GET /api/v1/client — one element per launcher platform.</summary>
public sealed record LauncherVersionDto(
    [property: JsonPropertyName("system")] SystemType System,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("md5")] byte[] Md5,
    [property: JsonPropertyName("size")] long Size);

/// <summary>GET /api/v1/game/{id} — full or incrementally filtered manifest.</summary>
public sealed record Content(
    [property: JsonPropertyName("categories")] IReadOnlyList<CategoryDto> Categories);

public sealed record CategoryDto(
    [property: JsonPropertyName("type")] ContentType Type,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("items")] IReadOnlyList<ItemDto> Items);

public sealed record ItemDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("files")] IReadOnlyList<FileDto> Files);

public sealed record FileDto(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("md5")] byte[] Md5,
    [property: JsonPropertyName("size")] long Size);
