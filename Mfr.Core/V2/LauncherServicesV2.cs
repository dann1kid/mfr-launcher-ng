using Mfr.Core.Network;
using Mfr.Core.Tasks;

namespace Mfr.Core.V2;

/// <summary>
/// Composition root for the v2 stack (Kotlin: KtorProvider + State v2): the v1
/// services stay available through Core for option/schema tasks that the dev
/// client also reuses via the schema bridge.
/// </summary>
public sealed class LauncherServicesV2
{
    public LauncherServicesV2(LauncherServices core, ApiClientV2 api, HttpFileDownloader downloader)
    {
        Core = core;
        Api = api;
        Downloader = downloader;
    }

    public LauncherServices Core { get; }

    public ApiClientV2 Api { get; }

    public HttpFileDownloader Downloader { get; }
}
