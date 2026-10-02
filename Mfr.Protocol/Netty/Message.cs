using Mfr.Protocol.Enums;

namespace Mfr.Protocol.Netty;

/// <summary>
/// Base type of the custom TCP file-transfer protocol messages
/// (Kotlin: <c>common.protocol.netty.Message</c>). Wire format is JSON with a
/// string type discriminator in property "id"; see <see cref="MessageCodec"/>.
/// </summary>
public abstract record Message
{
    internal abstract string TypeDiscriminator { get; }
}

/// <summary>id "1" — client asks the server to stream the given game files.</summary>
public sealed record RequestGameFilesMessage(Guid ClientId, IReadOnlyList<int> Files) : Message
{
    internal override string TypeDiscriminator => "1";
}

/// <summary>id "2" — server sends one chunk of a file.</summary>
public sealed record UploadFileMessage(
    int FileId,
    long Position,
    byte[] Data,
    bool LastFrame) : Message
{
    public UploadFileMessage(int fileId, long position, byte[] data) : this(fileId, position, data, false) { }

    internal override string TypeDiscriminator => "2";
}

/// <summary>id "3" — server closes the session after the last frame.</summary>
public sealed record EndSessionMessage() : Message
{
    internal override string TypeDiscriminator => "3";
}

/// <summary>id "4" — server reports a failed transfer.</summary>
public sealed record ServerExceptionMessage(string? Text = null, Guid? Uuid = null) : Message
{
    internal override string TypeDiscriminator => "4";
}

/// <summary>id "5" — client asks the server to stream the launcher binary.</summary>
public sealed record RequestLauncherFilesMessage(Guid ClientId, SystemType SystemType) : Message
{
    internal override string TypeDiscriminator => "5";
}

/// <summary>id "6" — server entered maintenance mode and will close the channel.</summary>
public sealed record ServerMaintenanceMessage() : Message
{
    internal override string TypeDiscriminator => "6";
}

/// <summary>id "7" — client pauses or resumes the download.</summary>
public sealed record RequestChangeState(bool Active) : Message
{
    internal override string TypeDiscriminator => "7";
}
