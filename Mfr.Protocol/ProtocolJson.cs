using System.Text.Json;
using System.Text.Json.Serialization;
using Mfr.Protocol.Json;

namespace Mfr.Protocol;

/// <summary>

/// string enums, base64 byte arrays, ISO-8601 local dates without timezone.
/// </summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters =
            {
                new JsonStringEnumConverter(),
                new LocalDateTimeJsonConverter(),
            },
        };
        return options;
    }
}
