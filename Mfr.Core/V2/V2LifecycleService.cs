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
/// v2 application lifecycle: loads persisted
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

    /// <summary>К-2: линия игрока исчезла с сервера — клиент перевёл её на первую доступную.</summary>
    public event Action<LineAutoChange>? LineAutoChanged;

    /// <summary>БТ-9: появилась (или исчезла) строго большая версия клиента.</summary>
    public event Action<bool>? LauncherUpdateAvailableChanged;

    /// <summary>Параметры события К-2.</summary>
    public sealed record LineAutoChange(string OldLine, string NewLine, bool GameInstalled);

    public bool ServerConnection { get; private set; }

    /// <summary>К-3: в текущей линии временно нет выпуска — кнопке нечего предлагать.</summary>
    public bool LineHasNothing { get; private set; }
    public Region Region => _services.Region;
    public IReadOnlyList<string> AvailableBuilds { get; private set; } = [];
    public GameUpdateStatus? GameUpdate { get; private set; }
    public string? NewLineAvailable { get; private set; }
    public string? ServerLauncherVersion { get; private set; }

    /// <summary>БТ-9: версия сервера строго больше своей по числовым октетам.</summary>
    public bool LauncherUpdateAvailable { get; private set; }
    public bool GameInstalled { get; private set; }
    public string GameVersion { get; private set; } = "";
    public ProtoSchema? InstalledSchema { get; private set; }

    /// <summary>Loads persisted state and starts the polling loops.</summary>
    public async Task InitializeAsync()
    {
        var repository = _services.Core.Repository;

        var location = repository.GetProperty(PropertyKeys.Location);
        if (location is "RU" or "EU")
        {
            _services.Region = Enum.Parse<Region>(location);
        }
        else
        {
            // БТ-10.4: регион не сохранён — единственный момент, когда измеряем /ping обоих
            var measured = await _services.Api.ChooseRegionAsync(_shutdown.Token).ConfigureAwait(false);
            _services.Region = measured;
            repository.SetProperty(PropertyKeys.Location, measured.ToString());
        }

        var knownBuilds = repository.GetProperty(PropertyKeys.KnownBuilds);
        if (!string.IsNullOrWhiteSpace(knownBuilds))
        {
            AvailableBuilds = knownBuilds.Split(',');
            AvailableBuildsChanged?.Invoke(AvailableBuilds);
        }

        GameInstalled = repository.GetProperty(PropertyKeys.NewReleaseInstalled) is not null ||
                        repository.GetProperty(PropertyKeys.GameInstalled) is not null;
        if (GameInstalled)
        {
            // БТ-5.1: порядок загрузки закрепляется на каждом старте
            Mfr.Core.Services.ModifyFiles.ApplyLoadOrderDates(_services.Core.Paths.Root);
            EnsureOpenMwConfig();
        }

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
        if (!_onlineMode)
        {
            return; // офлайн-режим: сервер не опрашивается (БТ-10)
        }
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
        if (_shutdown.IsCancellationRequested || !_onlineMode)
        {
            return;
        }
        try
        {
            // БТ-10.4: регион — сохранённый выбор; проверяем доступность только его
            var chosen = _services.Api.Base(_services.Region);
            var online = await _services.Api.PingAsync(chosen, _shutdown.Token).ConfigureAwait(false) is not null;
            if (online != ServerConnection)
            {
                ServerConnection = online;
                ServerConnectionChanged?.Invoke(online);
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
    ///.
    /// </summary>
    public async Task RefreshStatusesAsync()
    {
        if (_shutdown.IsCancellationRequested || !_onlineMode || !ServerConnection)
        {
            return;
        }
        // GameInstalled мог быть записан задачей — перечитываем (фикс: кнопка
        // «Установить игру» после частично успешной установки)
        var persistedInstalled = _services.Core.Repository.GetProperty(PropertyKeys.NewReleaseInstalled) is not null ||
                                 _services.Core.Repository.GetProperty(PropertyKeys.GameInstalled) is not null;
        if (persistedInstalled != GameInstalled)
        {
            GameInstalled = persistedInstalled;
            GameInstalledChanged?.Invoke(persistedInstalled);
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
                    LineHasNothing = result.State == LineVersionState.NothingYet;
                    if (result.State == LineVersionState.Found && result.Id is not null)
                    {
                        var status = new GameUpdateStatus(InstalledSchema.Version, result.Id);
                        if (status.NeedUpdate != (GameUpdate?.NeedUpdate ?? false) || GameUpdate?.ServerVersion != result.Id)
                        {
                            GameUpdate = status;
                            GameUpdateStatusChanged?.Invoke(status);
                        }
                    }
                    else if (result.State == LineVersionState.NoSuchLine &&
                             AvailableBuilds is { Count: > 0 } available &&
                             available[0] != line)
                    {
                        // К-2: сервер не знает линии — переводим на первую доступную без выбора игрока
                        var target = available[0];
                        repository.SetProperty(PropertyKeys.SelectedBuild, target);
                        LineAutoChanged?.Invoke(new LineAutoChange(line, target, GameInstalled));
                        GameUpdate = null;
                        GameUpdateStatusChanged?.Invoke(null);
                        // в новой линии состав другой — состав установленной версии больше не считается актуальным
                        NewLineAvailable = null;
                    }
                    // NothingYet (К-3): линии временно нечего отдать — ничего не меняем,
                    // кнопка ничего не предлагает, опрос повторится по таймеру
                    LineHasNothing = result.State == LineVersionState.NothingYet;
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

            // NOTE: the server's launcher version tracks the JAVA client; the native
            // client's own update channel is defined separately (GitHub release or a
            // dedicated server field) — surfaced for information, never a nag prompt
            var launcherVersion = await _services.Api.GetLauncherVersion(_services.Region, _shutdown.Token).ConfigureAwait(false);
            if (launcherVersion != ServerLauncherVersion)
            {
                ServerLauncherVersion = launcherVersion;
                LauncherVersionChanged?.Invoke(launcherVersion);
            }
            // ВЫКЛЮЧЕНО: на сервере зарегистрирован Java-клиент (3.3.1), не наш нативный.
            // Включить после регистрации нативного клиента автором.
            var updateAvailable = false; // LauncherVersions.IsStrictlyGreater(launcherVersion, _services.Core.Options.Version);
            if (updateAvailable != LauncherUpdateAvailable)
            {
                LauncherUpdateAvailable = updateAvailable;
                LauncherUpdateAvailableChanged?.Invoke(updateAvailable);
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
        // Morrowind orders plugins by file date: re-pin the load order
        Mfr.Core.Services.ModifyFiles.ApplyLoadOrderDates(_services.Core.Paths.Root);
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

    /// <summary>
    /// На Linux OpenMW читает ~/.config/openmw/openmw.cfg — если конфига нет
    /// (чистая установка, пользователь заменил папку), генерируем и применяем
    /// пресет «Средний» с правильным путём. На Windows настройки в Documents.
    /// </summary>
    private void EnsureOpenMwConfig()
    {
        try
        {
            var configPath = Path.Combine(_services.Core.Paths.OpenMwConfigFolder, "openmw.cfg");
            if (File.Exists(configPath))
            {
                return; // конфиг уже есть — пользователь мог настроить
            }
            _services.Core.OpenMw.PrepareTemplates();
            _services.Core.OpenMw.ApplyConfig(OpenMwConfiguration.MIDDLE, backup: false);
        }
        catch (Exception)
        {
            // автонастройка — best effort, не мешает запуску
        }
    }

    private volatile bool _onlineMode = true;

    /// <summary>Смена региона игроком: проверить доступность нового и перезапросить статусы.</summary>
    public async Task ChangeRegionAsync(Region region)
    {
        _services.Region = region;
        _services.Core.Repository.SetProperty(PropertyKeys.Location, region.ToString());
        LocationChanged?.Invoke(region);
        ServerConnection = false;
        await CheckServerAsync().ConfigureAwait(false);
        if (ServerConnection)
        {
            await RefreshStatusesAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Offline mode stops all server polling.</summary>
    public void SetOnlineMode(bool online)
    {
        _onlineMode = online;
        if (online)
        {
            _serverTimer.Start();
            _statusTimer.Start();
        }
        else
        {
            _serverTimer.Stop();
            _statusTimer.Stop();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _serverTimer.Dispose();
        _statusTimer.Dispose();
    }
}
