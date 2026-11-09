using Mfr.Protocol.Enums;

namespace Mfr.Core.Options;

/// <summary>
/// Launcher settings; defaults mirror the old application.yml.
/// Overridable from launcher.json next to the executable (wired up in the app phase).
/// </summary>
public sealed record LauncherOptions
{
    /// <summary>Версия клиента; единственный источник — Directory.Build.props, сюда попадает из сборки.</summary>
    public string Version { get; init; } = "";

    public SystemType Platform { get; init; } = SystemType.WINDOWS;

    /// <summary>
    /// Client identity sent in the Identity header / X-Client-ID. Empty by default:
    /// LauncherServices fills it from launcher.ini (which keeps the UUID stable across
    /// updates — server-side statistics and beta-channel access key on it). An
    /// explicitly provided id wins (tests).
    /// </summary>
    public Guid ClientId { get; init; }

    public ServerOptions Server { get; init; } = new();
}

public sealed class ServerOptions
{
    /// <summary>Адреса живут только в файле адресов (addresses.json) и его копии launcher.json рядом с exe.</summary>
    public string Address { get; init; } = "";

    public string RuLocationAddress { get; init; } = "";

    public string EuLocationAddress { get; init; } = "";

    public int TcpPort { get; init; } = 9020;

    public int ConnectionCount { get; init; } = 20;
}
