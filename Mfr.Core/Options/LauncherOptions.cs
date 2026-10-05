using Mfr.Protocol.Enums;

namespace Mfr.Core.Options;

/// <summary>
/// Launcher settings; defaults mirror the old application.yml.
/// Overridable from launcher.json next to the executable (wired up in the app phase).
/// </summary>
public sealed record LauncherOptions
{
    public string Version { get; init; } = "4.0.0";

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
    public string Address { get; init; } = "mfr.fullrest.ru";

    /// <summary>v2 regions (dev application.yml), used once the server answers /v2.</summary>
    public string RuLocationAddress { get; init; } = "client.mfr.yc.lezenford.com";

    public string EuLocationAddress { get; init; } = "client.mfr.hz.lezenford.com";

    public int TcpPort { get; init; } = 9020;

    public int ConnectionCount { get; init; } = 20;
}
