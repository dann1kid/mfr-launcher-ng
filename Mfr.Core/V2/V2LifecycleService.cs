using Mfr.Core.Network;
using Mfr.Core.Storage;
using Mfr.Core.Tasks;
using Mfr.Protocol.Dto;
using ProtoSchema = Com.Lezenford.Mfr.Schema.V1.Schema;

namespace Mfr.Core.V2;

/// <summary>Game update availability as seen by the v2 lifecycle.</summary>
public sealed record GameUpdateStatus(string CurrentVersion, string? ServerVersion)
{
    public bool NeedUpdate => ServerVersion is not null && ServerVersion != CurrentVersion;
}

/// <summary>
/// v2 application lifecycle (Kotlin: InitApplicationInitiator): loads persisted
/// state, restores the installed schema, and runs the online polling loops —
/// region availability, compatibility lines, game/launcher update statuses.
/// Exposes observable state for the UI layer.
/// </summary>
public sealed class V2LifecycleService : IDisposable
{
    private readonly LauncherServicesV2 _services;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly System.Timers.Timer _serverTimer = new(TimeSpan.FromMinutes(5));
    private readonly System.Timers.Timer _statusTimer = new(TimeSpan.FromHours(1));

    public V2LifecycleService(LauncherServicesV2 services)
    {
        _services = services;
    }

    public event Action<bool>? ServerConnectionChanged;
    public event Action<Region>? LocationChanged;
    public event Action<IReadOnlyList<string>>? AvailableBuildsChanged;
    public event Action<GameUpdateStatus?>? GameUpdateStatusChanged;
    public event Action<string?>? NewLineAvailableChanged;
    public event Action<string?>? LauncherVersionChanged;
    public event Action<bool>? GameInstalledChanged;
    public event Action<string>? GameVersionChanged;

    public bool ServerConnection { get; private set; }
    public Region Region => _services.Region;
    public IReadOnlyList<string> AvailableBuilds { get; private set; } = [];
    public GameUpdateStatus? GameUpdate { get; private set; }
    public string? NewLineAvailable { get; private set; }
    public string? ServerLauncherVersion { get; private set; }
    public bool GameInstalled { get; private set; }
    public string GameVersion { get; private set; } = "";
    public ProtoSchema? InstalledSchema { get; private set; }

    /// <summary>Loads persisted state and starts the polling loops (Kotlin: init).</summary>
    public async Task InitializeAsync()
    {
        var repository = _services.Core.Repository;

        var location = repository.GetProperty(PropertyKeys.Location);
        if (location is "RU" or "EU")
        {
            _services.Region = Enum.Parse<Region>(location);
        }

        var knownBuilds = repository.GetProperty(PropertyKeys.KnownBuilds);
        if (!string.IsNullOrWhiteSpace(knownBuilds))
        {
            AvailableBuilds = knownBuilds.Split(',');
            AvailableBuildsChanged?.Invoke(AvailableBuilds);
        }

        GameInstalled = repository.GetProperty(PropertyKeys.NewReleaseInstalled) is not null ||
                        repository.GetProperty(PropertyKeys.GameInstalled) is not null;

        var schema = V2Manifests.TryLoadLocalSchema(_services.Core);
        if (schema is not null)
        {
            InstalledSchema = schema;
            GameVersion = schema.Version;
            GameVersionChanged?.Invoke(GameVersion);
            if (repository.GetProperty(PropertyKeys.SelectedBuild) is null)
            {
                var line = VersionLine.Of(schema.Version);
                if (line is not null)
                {
                    repository.SetProperty(PropertyKeys.SelectedBuild, line);
                }
            }
        }

        _serverTimer.Elapsed += async (_, _) => await CheckServerAsync().ConfigureAwait(false);
        _statusTimer.Elapsed += async (_, _) => await RefreshStatusesAsync().ConfigureAwait(false);
        _serverTimer.Start();
        _statusTimer.Start();

        await CheckServerAsync().ConfigureAwait(false);
        if (ServerConnection)
        {
            await RefreshStatusesAsync().ConfigureAwait(false);
        }
    }

    private async Task CheckServerAsync()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }
        try
        {
            var ru = await _services.Api.PingAsync(_services.Api.RuBase, _shutdown.Token).ConfigureAwait(false);
            var eu = await _services.Api.PingAsync(_services.Api.EuBase, _shutdown.Token).ConfigureAwait(false);
            var online = ru is not null || eu is not null;
            if (online != ServerConnection)
            {
                ServerConnection = online;
                ServerConnectionChanged?.Invoke(online);
            }
            // stick to the reachable region when only one answers (Kotlin parity)
            if (eu is null && ru is not null && _services.Region == Region.EU)
            {
                SwitchRegion(Region.RU);
            }
            else if (ru is null && eu is not null && _services.Region == Region.RU)
            {
                SwitchRegion(Region.EU);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // network errors just mean "offline for now"
        }
    }

    private void SwitchRegion(Region region)
    {
        _services.Region = region;
        _services.Core.Repository.SetProperty(PropertyKeys.Location, region.ToString());
        LocationChanged?.Invoke(region);
    }

    /// <summary>
    /// One pass of the hourly status refresh: channels, game version of the
    /// selected line, freshest-line notice, launcher version
    /// (Kotlin: the streamUpdateSubscribe blocks).
    /// </summary>
    public async Task RefreshStatusesAsync()
    {
        if (_shutdown.IsCancellationRequested || !ServerConnection)
        {
            return;
        }
        try
        {
            var repository = _services.Core.Repository;
            var channels = await _services.Api.GetChannels(_services.Region, _shutdown.Token).ConfigureAwait(false);
            if (!channels.SequenceEqual(AvailableBuilds))
            {
                AvailableBuilds = channels;
                AvailableBuildsChanged?.Invoke(channels);
                repository.SetProperty(PropertyKeys.KnownBuilds, string.Join(',', channels));
            }

            if (GameInstalled && InstalledSchema is not null)
            {
                var line = repository.GetProperty(PropertyKeys.SelectedBuild)
                    ?? VersionLine.Of(InstalledSchema.Version);
                if (line is not null)
                {
                    var result = await _services.Api.GetGameVersion(line, _services.Region, _shutdown.Token).ConfigureAwait(false);
                    if (result.State == LineVersionState.Found && result.Id is not null)
                    {
                        var status = new GameUpdateStatus(InstalledSchema.Version, result.Id);
                        if (status.NeedUpdate != (GameUpdate?.NeedUpdate ?? false) || GameUpdate?.ServerVersion != result.Id)
                        {
                            GameUpdate = status;
                            GameUpdateStatusChanged?.Invoke(status);
                        }
                    }
                }

                // the freshest line is highlighted until the player dismisses it
                var newest = channels.OrderByDescending(c => c, Comparer<string>.Create(VersionLine.Compare)).FirstOrDefault();
                var dismissed = repository.GetProperty(PropertyKeys.DismissedBuild);
                var currentLine = repository.GetProperty(PropertyKeys.SelectedBuild);
                var notice = newest is not null && currentLine is not null &&
                             VersionLine.Compare(newest, currentLine) > 0 && newest != dismissed
                    ? newest
                    : null;
                if (notice != NewLineAvailable)
                {
                    NewLineAvailable = notice;
                    NewLineAvailableChanged?.Invoke(notice);
                }
            }

            var launcherVersion = await _services.Api.GetLauncherVersion(_services.Region, _shutdown.Token).ConfigureAwait(false);
            if (launcherVersion != ServerLauncherVersion)
            {
                ServerLauncherVersion = launcherVersion;
                LauncherVersionChanged?.Invoke(launcherVersion);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // a failed refresh retries on the next tick
        }
    }

    /// <summary>Called after a successful install/update: refresh the local schema state.</summary>
    public void OnGameInstalled()
    {
        GameInstalled = true;
        GameInstalledChanged?.Invoke(true);
        var schema = V2Manifests.TryLoadLocalSchema(_services.Core);
        if (schema is not null)
        {
            InstalledSchema = schema;
            GameVersion = schema.Version;
            GameVersionChanged?.Invoke(GameVersion);
        }
        _ = RefreshStatusesAsync();
    }

    /// <summary>Dismisses the "new line available" notice until a newer one appears.</summary>
    public void DismissNewLine()
    {
        if (NewLineAvailable is { } line)
        {
            _services.Core.Repository.SetProperty(PropertyKeys.DismissedBuild, line);
            NewLineAvailable = null;
            NewLineAvailableChanged?.Invoke(null);
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _serverTimer.Dispose();
        _statusTimer.Dispose();
    }
}
