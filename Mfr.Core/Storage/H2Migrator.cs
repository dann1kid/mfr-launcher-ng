namespace Mfr.Core.Storage;

/// <summary>
/// Best-effort property import from the old client's H2 database (launcher.mv.db).
/// The MVStore pages keep table rows as readable fragments, so known PROPERTY keys
/// can be located by scanning for the key followed by a serialization marker and a
/// printable value. Only used when our SQLite store is fresh — first run over an
/// existing old installation.
/// </summary>
public static class H2Migrator
{
    private static readonly string[] ImportKeys =
    [
        PropertyKeys.SelectedBuild,
        PropertyKeys.KnownBuilds,
        PropertyKeys.Location,
        PropertyKeys.OnlineMode,
        PropertyKeys.MinimizeToTray,
        PropertyKeys.SpeedLimit,
        PropertyKeys.LastUpdateDate,
        PropertyKeys.Schema,
        PropertyKeys.GameInstalled,
        PropertyKeys.NewReleaseInstalled,
        PropertyKeys.OldReleaseRemoved,
        PropertyKeys.DismissedBuild,
    ];

    /// <summary>Returns the imported property map, empty when nothing readable was found.</summary>
    public static IReadOnlyDictionary<string, string> ReadProperties(string mvDbPath)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(mvDbPath))
        {
            return result;
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(mvDbPath);
        }
        catch (IOException)
        {
            return result;
        }

        foreach (var key in ImportKeys)
        {
            var value = FindLatestValue(data, key);
            if (value is not null)
            {
                result[key] = value;
            }
        }
        return result;
    }

    /// <summary>
    /// The MVStore appends row versions and repeats key names in page indexes, so a
    /// plain "last printable run wins" grabs bookkeeping bytes. Collect every
    /// candidate and keep the LAST one whose shape matches the key's value type.
    /// </summary>
    private static string? FindLatestValue(byte[] data, string key)
    {
        var keyBytes = System.Text.Encoding.ASCII.GetBytes(key);
        var matcher = Matcher(key);
        string? latest = null;
        var index = 0;
        while ((index = IndexOf(data, keyBytes, index)) >= 0)
        {
            // the stored form is KEY + length/marker bytes + ASCII value; H2 prefixes
            // values with their length, so look at the printable runs starting at the
            // next few offsets
            for (var skip = 0; skip <= 2; skip++)
            {
                var start = index + keyBytes.Length + skip;
                var end = start;
                while (end < data.Length && end - start < 128 && data[end] is >= 0x20 and <= 0x7E)
                {
                    end++;
                }
                if (end > start)
                {
                    var candidate = System.Text.Encoding.ASCII.GetString(data, start, end - start);
                    var shaped = matcher(candidate);
                    if (shaped is not null)
                    {
                        latest = shaped;
                    }
                }
            }
            index += keyBytes.Length;
        }
        return latest;
    }

    private static System.Func<string, string?> Matcher(string key) => key switch
    {
        PropertyKeys.SelectedBuild or PropertyKeys.KnownBuilds or PropertyKeys.DismissedBuild =>
            value => VersionShape(value) ? value : null,
        PropertyKeys.Location => value => value is "RU" or "EU" ? value : null,
        PropertyKeys.OnlineMode or PropertyKeys.MinimizeToTray =>
            value => value is "true" or "false" ? value : null,
        PropertyKeys.GameInstalled or PropertyKeys.NewReleaseInstalled or PropertyKeys.OldReleaseRemoved =>
            value => value is "true" or "false" ? value : null,
        PropertyKeys.LastUpdateDate => value =>
            System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}") ? value : null,
        PropertyKeys.SpeedLimit => value =>
            System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{1,7}$") ? value : null,
        PropertyKeys.Schema => value => value.Length is 32 or 64 &&
            value.All(c => Uri.IsHexDigit(c)) ? value : null,
        _ => value => null,
    };

    /// <summary>Version lines look like "5.0"; build lists are comma-separated ones.</summary>
    private static bool VersionShape(string value) =>
        value.Length > 0 && value.Split(',').All(part =>
            // compatibility lines always carry a major.minor: "5.0" — never a bare digit
            System.Text.RegularExpressions.Regex.IsMatch(part, @"^\d{1,3}\.\d{1,3}$"));

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        for (var i = from; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return i;
            }
        }
        return -1;
    }
}
