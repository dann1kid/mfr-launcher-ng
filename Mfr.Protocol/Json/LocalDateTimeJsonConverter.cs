using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mfr.Protocol.Json;

/// <summary>
/// Jackson serializes <c>LocalDateTime</c> (WRITE_DATES_AS_TIMESTAMPS off) as
/// <c>2024-01-01T18:06:39</c>, omitting zero seconds (<c>2024-01-01T18:06</c>)
/// and zero nanos. The launcher must read and write the same shapes.
/// </summary>
public sealed class LocalDateTimeJsonConverter : JsonConverter<DateTime>
{
    private static readonly string[] ReadFormats =
    [
        "yyyy-MM-ddTHH:mm:ss.fffffff",
        "yyyy-MM-ddTHH:mm:ss.ffffff",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd",
    ];

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString()
            ?? throw new JsonException("Expected a string date value, got null.");
        foreach (var format in ReadFormats)
        {
            if (DateTime.TryParseExact(s, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
                return value;
        }
        throw new JsonException($"Unexpected LocalDateTime format: '{s}'.");
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var pattern = value.Second == 0 && value.Millisecond == 0
            ? "yyyy-MM-ddTHH:mm"
            : "yyyy-MM-ddTHH:mm:ss";
        writer.WriteStringValue(value.ToString(pattern, CultureInfo.InvariantCulture));
    }
}
