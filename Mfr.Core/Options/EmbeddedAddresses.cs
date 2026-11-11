using System.Reflection;
using System.Text.Json;

namespace Mfr.Core.Options;

/// <summary>
/// Адреса, встроенные в сборку: файл адресов целиком и контур, выбранный при сборке
/// (свойство MfrContour). Игроку уходит один exe, поэтому без них клиент не знал бы,
/// куда идти; держать вторую копию хостов в коде нельзя — у адресов одно место.
/// </summary>
public static class EmbeddedAddresses
{
    private const string ResourceName = "addresses.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Контур, чья секция встроена: production, если сборке его не назвали.</summary>
    public static string Contour { get; } =
        typeof(EmbeddedAddresses).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "MfrContour")?.Value is { Length: > 0 } contour
            ? contour
            : "production";

    /// <summary>Секция встроенного контура; null — ресурса или секции нет.</summary>
    public static ServerOptions? Current()
    {
        using var stream = typeof(EmbeddedAddresses).Assembly.GetManifestResourceStream(ResourceName);
        return stream is null ? null : Read(stream, Contour);
    }

    /// <summary>Адреса контура из файла адресов; null — такого контура в файле нет.</summary>
    public static ServerOptions? Read(Stream addresses, string contour)
    {
        using var document = JsonDocument.Parse(addresses);
        if (!document.RootElement.TryGetProperty(contour, out var section))
        {
            return null;
        }
        return section.Deserialize<LauncherOptions>(JsonOptions)?.Server;
    }
}
