using System.Security.Cryptography;
using ProtoSchema = Com.Lezenford.Mfr.Schema.V1.Schema;

namespace Mfr.Core.V2;

/// <summary>SHA-256 helpers — the integrity algorithm of the v2 manifests.</summary>
public static class Sha256
{
    public static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return System.Security.Cryptography.SHA256.HashData(stream);
    }

    public static bool Matches(string path, ReadOnlySpan<byte> expected) => Hash(path).AsSpan().SequenceEqual(expected);
}

/// <summary>
/// Compatibility-line helpers (Kotlin: VersionLineExtension): a line is the two
/// first octets of a version (`1.5.3` → `1.5`), octets compared as numbers.
/// </summary>
public static class VersionLine
{
    public static string? Of(string? version)
    {
        var octets = Octets(version);
        return octets is { } pair ? $"{pair.Major}.{pair.Minor}" : null;
    }

    public static int Compare(string left, string right)
    {
        var a = Octets(left);
        var b = Octets(right);
        if (a is not null && b is not null)
        {
            return a.Value.Major != b.Value.Major ? a.Value.Major.CompareTo(b.Value.Major)
                : a.Value.Minor.CompareTo(b.Value.Minor);
        }
        if (a is not null)
        {
            return 1;
        }
        if (b is not null)
        {
            return -1;
        }
        return string.CompareOrdinal(left, right);
    }

    private static (int Major, int Minor)? Octets(string? version)
    {
        if (version is null)
        {
            return null;
        }
        var parts = version.Trim().Split('.');
        if (parts.Length < 2 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor))
        {
            return null;
        }
        return (major, minor);
    }
}

/// <summary>
/// Bridge from the protobuf schema to the launcher's option model
/// (Kotlin: Schema.toOldSchema): mainPath→storage, optionalPath→game,
/// sha256→hash. Lets the existing option tasks keep working unchanged.
/// </summary>
public static class SchemaBridge
{
    public static IReadOnlyList<Storage.Section> ToSections(ProtoSchema protoSchema)
    {
        var sections = new List<Storage.Section>();
        foreach (var option in protoSchema.Options)
        {
            var section = new Storage.Section(0, option.Name, Downloaded: false);
            foreach (var content in option.Contents)
            {
                var created = new Storage.Option(0, 0, option.Name, content.Name,
                    Applied: false, content.Description ?? string.Empty, content.PicturePath);
                foreach (var file in content.Partition.Files)
                {
                    created.Files.Add(new Storage.OptionFile(0, 0,
                        file.MainPath, file.OptionalPath ?? file.MainPath, file.Sha256.ToByteArray()));
                }
                section.Options.Add(created);
            }
            sections.Add(section);
        }
        return sections;
    }
}
