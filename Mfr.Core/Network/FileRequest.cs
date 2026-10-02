namespace Mfr.Core.Network;

/// <summary>
/// A file to download over the TCP protocol: server-side manifest id plus the
/// local target path and the expected checksum from the manifest.
/// </summary>
public sealed record FileRequest(int Id, string Path, long Size, byte[] Md5)
{
    public static FileRequest Launcher(string path, long size, byte[] md5)
        => new(0, path, size, md5);
}
