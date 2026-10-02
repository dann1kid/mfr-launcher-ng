using System.Text;
using System.Text.Json;
using Mfr.Protocol.Enums;

namespace Mfr.Protocol.Netty;

/// <summary>
/// Encodes and decodes TCP protocol messages exactly as the Kotlin/Jackson
/// server does. Wire quirks that MUST be preserved:
/// <list type="bullet">
/// <item>the discriminator is a string property "id":"1".."7" written FIRST;</item>
/// <item>messages 1, 2 and 5 also carry their payload in a property named "id",
/// so the JSON contains a DUPLICATE "id" key: string discriminator, then value;</item>
/// <item>fields equal to their constructor defaults are omitted
/// (Jackson NON_DEFAULT): position 0, empty data, lastFrame=false, null text/uuid;</item>
/// <item>lastFrame is a NUMBER 0/1, not a boolean;</item>
/// <item>byte data is a base64 string; enums and UUIDs are plain strings.</item>
/// </list>
/// </summary>
public static class MessageCodec
{
    public static byte[] Encode(Message message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteMessage(writer, message);
        return stream.ToArray();
    }

    public static Message Decode(byte[] utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Protocol message must be a JSON object, got {document.RootElement.ValueKind}.");

        var first = true;
        var discriminator = default(string?);
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (first)
            {
                first = false;
                if (property.Name == "id" && property.Value.ValueKind == JsonValueKind.String)
                {
                    discriminator = property.Value.GetString();
                    continue; // the duplicate payload "id" is picked up below
                }
                throw new JsonException($"Protocol message must start with the \"id\" discriminator, got '{property.Name}'.");
            }
            fields[property.Name] = property.Value; // later duplicate "id" overwrites nothing: discriminator was skipped
        }

        return discriminator switch
        {
            "1" => new RequestGameFilesMessage(
                ReadGuid(fields, "id"),
                ReadIntList(fields, "f")),
            "2" => new UploadFileMessage(
                ReadInt(fields, "id"),
                fields.TryGetValue("p", out var p) ? p.GetInt64() : 0L,
                fields.TryGetValue("d", out var d) ? Convert.FromBase64String(d.GetString()!) : [],
                fields.TryGetValue("l", out var l)
                    ? l.ValueKind == JsonValueKind.Number ? l.GetInt32() != 0 : l.GetBoolean()
                    : false),
            "3" => new EndSessionMessage(),
            "4" => new ServerExceptionMessage(
                fields.TryGetValue("text", out var text) && text.ValueKind != JsonValueKind.Null ? text.GetString() : null,
                fields.TryGetValue("uuid", out var uuid) && uuid.ValueKind != JsonValueKind.Null ? ReadUuid(uuid) : null),
            "5" => new RequestLauncherFilesMessage(
                ReadGuid(fields, "id"),
                Enum.Parse<SystemType>(fields["type"].GetString() ?? throw new JsonException("Missing 'type'."), ignoreCase: false)),
            "6" => new ServerMaintenanceMessage(),
            "7" => new RequestChangeState(fields["active"].GetBoolean()),
            _ => throw new JsonException($"Unknown protocol message discriminator '{discriminator}'."),
        };
    }

    private static void WriteMessage(Utf8JsonWriter writer, Message message)
    {
        writer.WriteStartObject();
        writer.WriteString("id", message.TypeDiscriminator); // duplicate "id" keys below are intentional wire format
        switch (message)
        {
            case RequestGameFilesMessage request:
                writer.WriteString("id", request.ClientId.ToString("D"));
                writer.WritePropertyName("f");
                writer.WriteStartArray();
                foreach (var fileId in request.Files)
                    writer.WriteNumberValue(fileId);
                writer.WriteEndArray();
                break;

            case UploadFileMessage upload:
                writer.WriteNumber("id", upload.FileId);
                if (upload.Position != 0L)
                    writer.WriteNumber("p", upload.Position);
                if (upload.Data.Length > 0)
                    writer.WriteString("d", Convert.ToBase64String(upload.Data));
                if (upload.LastFrame)
                    writer.WriteNumber("l", 1);
                break;

            case EndSessionMessage:
                break;

            case ServerExceptionMessage error:
                if (error.Text is not null)
                    writer.WriteString("text", error.Text);
                if (error.Uuid is not null)
                    writer.WriteString("uuid", error.Uuid.Value.ToString("D"));
                break;

            case RequestLauncherFilesMessage launcherRequest:
                writer.WriteString("id", launcherRequest.ClientId.ToString("D"));
                writer.WriteString("type", launcherRequest.SystemType.ToString());
                break;

            case ServerMaintenanceMessage:
                break;

            case RequestChangeState changeState:
                writer.WriteBoolean("active", changeState.Active);
                break;

            default:
                throw new ArgumentException($"Unknown message type {message.GetType().Name}.");
        }
        writer.WriteEndObject();
    }

    private static Guid ReadGuid(Dictionary<string, JsonElement> fields, string name) => ReadUuid(fields[name]);

    private static Guid ReadUuid(JsonElement element) => Guid.Parse(element.GetString()!);

    private static int ReadInt(Dictionary<string, JsonElement> fields, string name) => fields[name].GetInt32();

    private static IReadOnlyList<int> ReadIntList(Dictionary<string, JsonElement> fields, string name)
    {
        var array = fields[name];
        var result = new int[array.GetArrayLength()];
        var index = 0;
        foreach (var item in array.EnumerateArray())
            result[index++] = item.GetInt32();
        return result;
    }
}
