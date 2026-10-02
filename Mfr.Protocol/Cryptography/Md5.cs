using System.Security.Cryptography;

namespace Mfr.Protocol.Cryptography;

/// <summary>
/// MD5 helpers — the launcher's integrity algorithm everywhere
/// (manifests, downloaded files, launcher self-update, schema.json).
/// Digests travel as 16 raw bytes, base64-encoded in JSON.
/// </summary>
public static class Md5
{
    public static byte[] Hash(Stream stream) => MD5.HashData(stream);

    public static byte[] Hash(ReadOnlySpan<byte> data) => MD5.HashData(data);

    public static bool Verify(Stream stream, ReadOnlySpan<byte> expected) => Hash(stream).AsSpan().SequenceEqual(expected);
}
