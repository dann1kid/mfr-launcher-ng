using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mfr.Core.Exceptions;
using Mfr.Core.Storage;
using Mfr.Core.Tasks;
using Mfr.Protocol.Dto;

namespace Mfr.Launcher.ViewModels;

/// <summary>
/// Main window state (Kotlin: LauncherController + ProgressComponent + State).
/// The update button is a state machine with the same statuses and texts as
/// the old client.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly LauncherServices _services;
    private readonly Mfr.Core.V2.LauncherServicesV2? _v2;
    private readonly SemaphoreSlim _taskLock = new(1, 1);
    private CancellationTokenSource? _taskCancellation;
    private UpdateStatus _lastTaskKind;

    public MainViewModel(LauncherServices? services = null, Mfr.Core.V2.LauncherServicesV2? v2 = null)
    {
        _services = services ?? new LauncherServices();
        _v2 = v2;
        GameVersion = _services.Paths.GameVersion;
        GamePath = _services.Paths.Root;
        // the settings tab shows THIS build's version, not the wire-level
        // compatibility string kept in LauncherOptions for server checks
        LauncherVersion = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        LauncherPath = AppContext.BaseDirectory;
    }

    public enum UpdateStatus
    {
        DISABLE,
        BLOCK,
        PAUSE,
        RESUME,
        GAME_UPDATE,
        GAME_INSTALL,
        LAUNCHER_UPDATE,
    }

    public enum ProgressState
    {
        Hidden,
        Empty,
        Enabled,
        Full,
        Disabled,
    }

    [ObservableProperty]
    private UpdateStatus _status = UpdateStatus.DISABLE;

    [ObservableProperty]
    private string _updateButtonText = "Обновлений нет";

    [ObservableProperty]
    private bool _updateButtonLocked = true;

    [ObservableProperty]
    private bool _updateButtonLava;

    [ObservableProperty]
    private bool _heartUpdate;

    [ObservableProperty]
    private int _percent;

    [ObservableProperty]
    private string _description = "";

    [ObservableProperty]
    private ProgressState _progress = ProgressState.Hidden;

    [ObservableProperty]
    private string _gameVersion = "";

    [ObservableProperty]
    private string _gamePath = "";

    [ObservableProperty]
    private string _launcherVersion = "";

    [ObservableProperty]
    private string _launcherPath = "";

    /// <summary>Set by MainWindow so dialogs can attach to it.</summary>
    public Avalonia.Controls.Window? Window { get; set; }

    public int CurrentBuildId { get; private set; } = 1;

    [ObservableProperty]
    private bool _showSettings;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _speedLimitEnabled;

    [ObservableProperty]
    private string _speedLimitKbText = "";

    [ObservableProperty]
    private bool _onlineMode = true;

    [ObservableProperty]
    private bool _consistencyEnabled;

    [ObservableProperty]
    private bool _isRuRegion = true;

    [ObservableProperty]
    private bool _isEuRegion;


    [ObservableProperty]
    private bool _gameSettingEnabled;

    [ObservableProperty]
    private bool _classicEnabled;

    [ObservableProperty]
    private bool _openMwEnabled;


    partial void OnIsRuRegionChanged(bool value)
    {
        if (value && _v2 is { } v2)
        {
            v2.Region = Mfr.Protocol.Dto.Region.RU;
            _services.Repository.SetProperty(Mfr.Core.Storage.PropertyKeys.Location, "RU");
            _ = _v2Lifecycle?.RefreshStatusesAsync();
        }
    }

    partial void OnIsEuRegionChanged(bool value)
    {
        if (value && _v2 is { } v2)
        {
            v2.Region = Mfr.Protocol.Dto.Region.EU;
            _services.Repository.SetProperty(Mfr.Core.Storage.PropertyKeys.Location, "EU");
            _ = _v2Lifecycle?.RefreshStatusesAsync();
        }
    }

    partial void OnSpeedLimitEnabledChanged(bool value) => ApplySpeedLimit();

    partial void OnSpeedLimitKbTextChanged(string value) => ApplySpeedLimit();

    private void ApplySpeedLimit()
    {
        var kb = int.TryParse(SpeedLimitKbText?.Trim(), out var parsed) ? parsed : 0;
        var limit = SpeedLimitEnabled && kb > 0 ? kb * 1024L : 0;
        _services.Downloader.SpeedLimitBytesPerSecond = limit;
        _v2?.Downloader.SetSpeedLimit(limit);
        _services.Repository.SetProperty(PropertyKeys.SpeedLimit,
            SpeedLimitEnabled && kb > 0 ? kb.ToString() : null);
    }

    private void LoadSettings()
    {
        MinimizeToTray = _services.Repository.GetProperty(PropertyKeys.MinimizeToTray) == "true";
        OnlineMode = _services.Repository.GetProperty(PropertyKeys.OnlineMode) != "false";
        if (int.TryParse(_services.Repository.GetProperty(PropertyKeys.SpeedLimit), out var kb) && kb > 0)
        {
            SpeedLimitKbText = kb.ToString();
            SpeedLimitEnabled = true;
        }
        ConsistencyEnabled = System.IO.File.Exists(_services.Paths.ClassicApplication);
        GameSettingEnabled = ConsistencyEnabled;
        ClassicEnabled = System.IO.File.Exists(_services.Paths.ClassicApplication);
        OpenMwEnabled = System.IO.File.Exists(_services.Paths.OpenMwApplication);
    }

    partial void OnStatusChanged(UpdateStatus value)
    {
        (UpdateButtonText, UpdateButtonLocked, UpdateButtonLava, HeartUpdate) = value switch
        {
            UpdateStatus.DISABLE => ("Обновлений нет", true, false, false),
            UpdateStatus.BLOCK => ("Подождите...", true, false, false),
            UpdateStatus.PAUSE => ("Приостановить", false, false, false),
            UpdateStatus.RESUME => ("Продолжить", false, false, false),
            UpdateStatus.GAME_UPDATE => ("Обновить игру", false, true, true),
            UpdateStatus.GAME_INSTALL => ("Установить игру", false, true, false),
            UpdateStatus.LAUNCHER_UPDATE => ("Обновить лаунчер", false, true, false),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };
    }

    /// <summary>
    /// Startup state detection (a simplified analogue of InitApplicationInitiator;
    /// the polling timer lands in phase 5): launcher update > missing game >
    /// changed build > nothing to do. When the server answers /v2 (dev 3.3.x),
    /// the v2 lifecycle owns the statuses instead.
    /// </summary>
    public async Task InitializeAsync()
    {
        LoadSettings();
        _ = RunPollingLoop();
        if (await TryActivateV2Async().ConfigureAwait(true))
        {
            return; // v2 lifecycle drives the UI state from here on
        }
        try
        {
            var builds = await _services.Api.GetBuilds();
            var build = Array.Find(builds, b => b.Default) ?? builds[0];
            CurrentBuildId = build.Id;
            _services.Repository.SetProperty(PropertyKeys.SelectedBuild, build.Id.ToString());

            if (_services.Repository.GetProperty(PropertyKeys.FirstStart) is null)
            {
                _services.Repository.SetProperty(PropertyKeys.FirstStart, "false");
                var welcome = new Views.MessageWindow("Благодарность",
                    "Дорогой друг!\n\nДобро пожаловать в Morrowind Fullrest Repack!\n\n" +
                    "Это не просто сборник модов, а тщательно подобранная и оптимизированная коллекция, " +
                    "дающая новую жизнь классической игре. На ее создание ушел не один год кропотливой работы " +
                    "большой команды людей, искренне любящих старый добрый Morrowind.\n\n" +
                    "Если вам понравится, не забудьте сказать авторам «спасибо». Ну а если что-то пойдет не так — " +
                    "мы ждем вас на форуме, в теме по багам.\n\nУдачи на просторах Вварденфелла!\nКоманда M[FR].\n\n" +
                    "Ну а теперь, Мы уже почти приплыли в Морровинд...");
                if (Window is { } window)
                {
                    await welcome.ShowDialog(window);
                }
            }

            var launchers = await _services.Api.GetLauncherVersions();
            var launcher = Array.Find(launchers, l => l.System == _services.Options.Platform);
            if (launcher is { } && IsNewer(launcher.Version, _services.Options.Version))
            {
                Status = UpdateStatus.LAUNCHER_UPDATE;
                return;
            }

            if (!System.IO.File.Exists(_services.Paths.ClassicApplication))
            {
                Status = UpdateStatus.GAME_INSTALL;
                return;
            }

            var stored = _services.Repository.GetProperty(PropertyKeys.LastUpdateDate);
            Trace($"status probe: classic={_services.Paths.ClassicApplication}:{System.IO.File.Exists(_services.Paths.ClassicApplication)} " +
                  $"stored={stored ?? "null"} build={build.LastUpdate:O}");
            if (stored is null ||
                (DateTime.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastUpdate) &&
                 build.LastUpdate > lastUpdate))
            {
                Status = UpdateStatus.GAME_UPDATE;
                return;
            }

            Status = UpdateStatus.DISABLE;
        }
        catch (ServerMaintenanceException)
        {
            Status = UpdateStatus.DISABLE;
        }
        catch (Exception)
        {
            // offline start: keep the game playable
            Status = System.IO.File.Exists(_services.Paths.ClassicApplication)
                ? UpdateStatus.DISABLE
                : UpdateStatus.GAME_INSTALL;
        }
    }

    // ── v2 activation (dev 3.3.x): probe once, then the v2 lifecycle owns the state ──

    private bool _v2Probed;
    private bool _v2Active;
    private Mfr.Core.V2.V2LifecycleService? _v2Lifecycle;

    private async Task<bool> TryActivateV2Async()
    {
        if (_v2 is not { } v2 || _v2Probed)
        {
            return _v2Active;
        }
        _v2Probed = true;
        try
        {
            // a failed probe against the v1 production resolves fast (unknown hosts)
            _ = await v2.Api.GetChannels(v2.Region).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Trace("v2 probe: server has no /v2, staying on the v1 path");
            return false; // v1 server — the classic path stays in charge
        }

        _v2Active = true;
        var lifecycle = new Mfr.Core.V2.V2LifecycleService(v2);
        _v2Lifecycle = lifecycle;

        lifecycle.GameUpdateStatusChanged += status =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_v2Active)
                {
                    return;
                }
                GameVersion = status?.CurrentVersion ?? GameVersion;
                Status = status is { NeedUpdate: true }
                    ? UpdateStatus.GAME_UPDATE
                    : UpdateStatus.DISABLE;
            });
        lifecycle.NewLineAvailableChanged += _ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_v2Active)
                {
                    return;
                }
                HeartUpdate = _v2Lifecycle?.NewLineAvailable is not null;
            });
        lifecycle.GameVersionChanged += version =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => GameVersion = version);
        lifecycle.LocationChanged += region =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (region == Mfr.Protocol.Dto.Region.RU)
                {
                    IsRuRegion = true;
                }
                else
                {
                    IsRuRegion = false;
                    IsEuRegion = true;
                }
            });

        // persisted region restores the radio state on startup
        IsEuRegion = v2.Region == Mfr.Protocol.Dto.Region.EU;

        Status = UpdateStatus.BLOCK;
        try
        {
            await lifecycle.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Trace($"v2 lifecycle init failed: {exception.Message}");
            // lifecycle failures keep the last known state; polling will recover
        }

        // v1→v2 migration: the legacy SELECTED_BUILD ("1") is not a compatibility
        // line — adopt the server's default channel (its first)
        var repository = _services.Repository;
        var line = repository.GetProperty(Mfr.Core.Storage.PropertyKeys.SelectedBuild);
        if ((line is null || !lifecycle.AvailableBuilds.Contains(line)) &&
            lifecycle.AvailableBuilds is { Count: > 0 } channels)
        {
            repository.SetProperty(Mfr.Core.Storage.PropertyKeys.SelectedBuild, channels[0]);
        }

        Status = lifecycle.InstalledSchema is null
            ? UpdateStatus.GAME_INSTALL
            : lifecycle.GameUpdate is { NeedUpdate: true }
                ? UpdateStatus.GAME_UPDATE
                : UpdateStatus.DISABLE;
        Trace($"v2 active: channels=[{string.Join(",", lifecycle.AvailableBuilds)}] " +
              $"schema={lifecycle.InstalledSchema?.Version ?? "none"} status={Status}");
        return true;
    }

    /// <summary>Polls builds and launcher versions every 5 minutes instead of RSocket subscriptions.</summary>
    private async Task RunPollingLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync())
        {
            if (!await _taskLock.WaitAsync(0))
            {
                continue; // a task is running; its completion re-detects the state anyway
            }
            _taskLock.Release();
            if (Status is UpdateStatus.GAME_INSTALL or UpdateStatus.LAUNCHER_UPDATE or UpdateStatus.GAME_UPDATE
                or UpdateStatus.DISABLE)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var previous = Status;
                    await InitializeAsync();
                    if (Status == previous && Progress == ProgressState.Full)
                    {
                        Progress = ProgressState.Hidden;
                    }
                });
            }
        }
    }

    internal static bool IsNewer(string candidate, string current)
    {
        if (Version.TryParse(candidate, out var newVersion) && Version.TryParse(current, out var oldVersion))
        {
            return newVersion > oldVersion;
        }
        return !string.Equals(candidate, current, StringComparison.Ordinal);
    }

    [RelayCommand(AllowConcurrentExecutions = true)] // stays clickable while a download runs
    private async Task Update()
    {
        Trace($"update command: status={Status} lastKind={_lastTaskKind} v2={_v2Active}");
        switch (Status)
        {
            case UpdateStatus.PAUSE:
                // the production server drops connections paused longer than ~0.6s,
                // so "pause" aborts the task; "resume" restarts it and MD5 skips
                // everything already downloaded
                Status = UpdateStatus.RESUME;
                Description = "Загрузка приостановлена";
                _taskCancellation?.Cancel();
                break;

            case UpdateStatus.RESUME:
                await RestartLastTask();
                break;

            case UpdateStatus.GAME_UPDATE:
                _lastTaskKind = UpdateStatus.GAME_UPDATE;
                await RestartLastTask();
                break;

            case UpdateStatus.GAME_INSTALL:
                _lastTaskKind = UpdateStatus.GAME_INSTALL;
                await RestartLastTask();
                break;

            case UpdateStatus.LAUNCHER_UPDATE:
                _lastTaskKind = UpdateStatus.LAUNCHER_UPDATE;
                await RestartLastTask();
                break;
        }
    }

    private async Task RestartLastTask()
    {
        if (_v2Active && _v2 is { } v2)
        {
            switch (_lastTaskKind)
            {
                case UpdateStatus.GAME_UPDATE:
                    await ExecuteTask(new Mfr.Core.V2.GameUpdateV2Task(v2), (t, ct) => t.Execute(null, ct), UpdateStatus.GAME_UPDATE);
                    return;
                case UpdateStatus.GAME_INSTALL:
                    await ExecuteTask(new Mfr.Core.V2.GameInstallV2Task(v2), (t, ct) => t.Execute(null, ct), UpdateStatus.GAME_INSTALL);
                    return;
            }
        }
        switch (_lastTaskKind)
        {
            case UpdateStatus.GAME_UPDATE:
            {
                var task = new UpdateGameTask(_services);
                await ExecuteTask(task, (t, ct) => t.Execute(1, ct), UpdateStatus.GAME_UPDATE);
                break;
            }
            case UpdateStatus.GAME_INSTALL:
            {
                var task = new InstallGameTask(_services);
                await ExecuteTask(task, (t, ct) => t.Execute(1, ct), UpdateStatus.GAME_INSTALL);
                break;
            }
            case UpdateStatus.LAUNCHER_UPDATE:
            {
                var task = new LauncherUpdateTask(_services);
                await ExecuteTask(task, (t, ct) => t.Execute(null, ct), UpdateStatus.LAUNCHER_UPDATE);
                break;
            }
        }
    }

    private Task ExecuteTask<TParam>(LauncherTask<TParam, object?> task, Func<LauncherTask<TParam, object?>, CancellationToken, Task> run, UpdateStatus kind)
    {
        _lastTaskKind = kind;
        return ExecuteTask(task, ct => run(task, ct));
    }

    private async Task ExecuteTask(LauncherTask task, Func<CancellationToken, Task> run)
    {
        if (!await _taskLock.WaitAsync(0))
        {
            return;
        }
        Trace($"task {task.GetType().Name} start");
        _taskCancellation = new CancellationTokenSource();
        Status = UpdateStatus.BLOCK;
        Progress = ProgressState.Empty;
        Percent = 0;
        ShowSettings = false; // switch to the main screen so the progress bar is visible
        task.ProgressChanged += OnTaskProgress;
        task.DescriptionChanged += OnTaskDescription;
        try
        {
            await run(_taskCancellation.Token);
            Trace($"task {task.GetType().Name} finished");
            Progress = ProgressState.Full;
            Percent = 100;
        }
        catch (OperationCanceledException oce)
        {
            Trace($"task cancelled: {oce.Message}");
            Progress = ProgressState.Hidden; // paused by the user
            return;
        }
        catch (Exception exception) when (exception is not ServerMaintenanceException)
        {
            Trace($"task FAILED: {exception}");
            Progress = ProgressState.Disabled;
            Description = exception is TaskExecuteException or DownloadFileException
                ? "Произошла ошибка: " + (exception.InnerException?.Message ?? exception.Message)
                : exception.Message;
        }
        finally
        {
            task.ProgressChanged -= OnTaskProgress;
            task.DescriptionChanged -= OnTaskDescription;
            _taskCancellation.Dispose();
            _taskCancellation = null;
            _taskLock.Release();
        }

        if (_v2Active && _v2Lifecycle is { } activeLifecycle)
        {
            // the v2 lifecycle reloads the schema and refreshes statuses itself
            activeLifecycle.OnGameInstalled();
        }
        else
        {
            await InitializeAsync();
            // tasks can deliver Optional\version (or change it); the footer must reflect that
            GameVersion = _services.Paths.GameVersion;
        }
    }

    // progress events come from worker tasks; marshal to the UI thread,
    // throttled to ~10 updates/sec so the UI thread stays responsive to clicks

    // written next to the exe, not the process working directory (which varies with launch method)
    private static readonly string TracePath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "task-trace.log");

    private static void Trace(string message) =>
        System.IO.File.AppendAllText(TracePath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}" + Environment.NewLine);

    private long _lastProgressPost;

    private void OnTaskProgress(int percent)
    {
        var now = System.Environment.TickCount64;
        if (percent < 100 && now - Volatile.Read(ref _lastProgressPost) < 100)
        {
            return;
        }
        Interlocked.Exchange(ref _lastProgressPost, now);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Progress = ProgressState.Enabled;
            Percent = percent;
            if (Status is UpdateStatus.BLOCK or UpdateStatus.RESUME)
            {
                Status = UpdateStatus.PAUSE; // download is running → allow pausing
            }
        });
    }

    private void OnTaskDescription(string description) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Description = description;
        });

    [RelayCommand]
    private void SwitchTab() => ShowSettings = !ShowSettings;

    [RelayCommand]
    private void ConfigureGame()
    {
        if (Window is not { } window)
        {
            return;
        }
        var viewModel = new GameOptionsViewModel(_services, this, () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ConsistencyEnabled = System.IO.File.Exists(_services.Paths.ClassicApplication);
        GameSettingEnabled = ConsistencyEnabled;
        ClassicEnabled = System.IO.File.Exists(_services.Paths.ClassicApplication);
        OpenMwEnabled = System.IO.File.Exists(_services.Paths.OpenMwApplication);
        }));
        var options = new Views.GameOptionsWindow { DataContext = viewModel };
        viewModel.DialogOwner = options; // dialogs show over the options window: the main one is hidden
        options.Closed += (_, _) => window.Show(); // the options screen replaces the main window
        window.Hide();
        options.Show();
    }

    [RelayCommand]
    private async Task CheckConsistency()
    {
        Trace("CheckConsistency command invoked");
        if (!ConsistencyEnabled)
        {
            Trace("CheckConsistency skipped: button disabled");
        }
        // the task body runs on a worker thread; windows must be created on the UI thread
        System.Func<string, System.Threading.CancellationToken, Task<bool>> askUser = async (question, _) =>
            Window is { } window &&
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => new Views.MessageWindow("Внимание", question, hasCancel: true).ShowDialog(window));
        if (_v2Active && _v2 is { } v2)
        {
            var task = new Mfr.Core.V2.CheckConsistencyV2Task(v2) { AskUser = askUser };
            await ExecuteTask(task, ct => task.Execute(null, ct));
        }
        else
        {
            var task = new CheckConsistencyTask(_services) { BuildId = CurrentBuildId, AskUser = askUser };
            await ExecuteTask(task, ct => task.Execute(null, ct));
        }
        ConsistencyEnabled = System.IO.File.Exists(_services.Paths.ClassicApplication);
        GameSettingEnabled = ConsistencyEnabled;
        ClassicEnabled = System.IO.File.Exists(_services.Paths.ClassicApplication);
        OpenMwEnabled = System.IO.File.Exists(_services.Paths.OpenMwApplication);
    }

    [RelayCommand]
    private void SwitchOnlineMode()
    {
        OnlineMode = !OnlineMode;
        _services.Repository.SetProperty(PropertyKeys.OnlineMode, OnlineMode ? "true" : "false");
    }

    [RelayCommand]
    private void ClassicGame()
    {
        _services.Runner.StartClassicGame();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Window?.WindowState = Avalonia.Controls.WindowState.Minimized);
    }

    [RelayCommand]
    private void ClassicLauncher() => _services.Runner.StartClassicLauncher();

    [RelayCommand]
    private void Mcp() => _services.Runner.StartMcp();

    [RelayCommand]
    private void Mge()
    {
        if (Window is { } window)
        {
            Views.PresetWindow.ForMge(_services.Mge, _services.Runner).Show(window);
        }
    }

    [RelayCommand]
    private void OpenMwGame()
    {
        _services.Runner.StartOpenMwGame();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Window?.WindowState = Avalonia.Controls.WindowState.Minimized);
    }

    [RelayCommand]
    private void OpenMwLauncher() => _services.Runner.StartOpenMwLauncher();

    [RelayCommand]
    private void OpenMwConfig()
    {
        if (Window is { } window)
        {
            Views.PresetWindow.ForOpenMw(_services.OpenMw).Show(window);
        }
    }

    [RelayCommand]
    private void OpenReadme() => OpenLink("https://mfr.fullrest.ru/readme");

    [RelayCommand]
    private void OpenForum() => OpenLink("https://www.fullrest.ru/forum/forum/300-morrowind-fullrest-repack-i-drugie-proekty-ot-ela/");

    [RelayCommand]
    private void OpenDonation()
    {
        if (Window is { } w) { new Views.DonationWindow().Show(w); }
    }

    [RelayCommand]
    private void OpenDiscord() => OpenLink("https://discord.gg/j2wrYTm");

    [RelayCommand]
    private void OpenYoutube() => OpenLink("https://www.youtube.com/channel/UCY0V-oKZPvv_SEnH5N8onSQ");

    [RelayCommand]
    private void OpenVk() => OpenLink("https://vk.com/club198345102");

    [RelayCommand]
    private void OpenPatreon() => OpenLink("https://www.patreon.com/aLMFR");


    [RelayCommand]
    private void OpenTelegram() => OpenLink("https://t.me/morrowindfr");

    [RelayCommand]
    private void OpenWebsite() => OpenLink("https://morrowindfr.com");

    private static void OpenLink(string url) =>
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

    public void Dispose()
    {
        _v2Lifecycle?.Dispose();
        _services.Dispose();
    }
}
