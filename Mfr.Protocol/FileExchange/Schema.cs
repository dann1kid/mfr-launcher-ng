using System.Text.Json.Serialization;

namespace Mfr.Protocol.FileExchange;

/// <summary>
/// schema.json — the option-system definition placed in the game folder by the
/// configurator (Kotlin: common.protocol.file.Schema). Every option file lives
/// twice on disk: a storage copy (storagePath) and the active game copy
/// (gamePath); applying an option copies storage→game.
/// </summary>
public sealed record SchemaFile(
    [property: JsonPropertyName("packages")] IReadOnlyList<SchemaPackage> Packages,
    [property: JsonPropertyName("extra")] IReadOnlyList<SchemaExtra> Extra);

public sealed record SchemaPackage(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("options")] IReadOnlyList<SchemaOption> Options);

public sealed record SchemaOption(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("image")] string? Image,
    [property: JsonPropertyName("items")] IReadOnlyList<SchemaOptionItem> Items);

public sealed record SchemaOptionItem(
    [property: JsonPropertyName("storagePath")] string StoragePath,
    [property: JsonPropertyName("gamePath")] string GamePath,
    [property: JsonPropertyName("md5")] byte[] Md5);

public sealed record SchemaExtra(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("items")] IReadOnlyList<SchemaExtraItem> Items);

public sealed record SchemaExtraItem(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("md5")] byte[] Md5);
