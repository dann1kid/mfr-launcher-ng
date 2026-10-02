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
        LauncherVersion = _services.Options.Version;
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
        }
    }

    partial void OnIsEuRegionChanged(bool value)
    {
        if (value && _v2 is { } v2)
        {
            v2.Region = Mfr.Protocol.Dto.Region.EU;
            _services.Repository.SetProperty(Mfr.Core.Storage.PropertyKeys.Location, "EU");
        }
    }

    partial void OnSpeedLimitEnabledChanged(bool value) => ApplySpeedLimit();

    partial void OnSpeedLimitKbTextChanged(string value) => ApplySpeedLimit();

    private void ApplySpeedLimit()
    {
        var kb = int.TryParse(SpeedLimitKbText?.Trim(), out var parsed) ? parsed : 0;
        _services.Downloader.SpeedLimitBytesPerSecond = SpeedLimitEnabled && kb > 0 ? kb * 1024L : 0;
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
    /// changed build > nothing to do.
    /// </summary>
    public async Task InitializeAsync()
    {
        LoadSettings();
        _ = RunPollingLoop();
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

        await InitializeAsync();
    }

    // progress events come from worker tasks; marshal to the UI thread,
    // throttled to ~10 updates/sec so the UI thread stays responsive to clicks

    private static void Trace(string message) =>
        System.IO.File.AppendAllText("task-trace.log", $"[{DateTime.Now:HH:mm:ss.fff}] {message}" + Environment.NewLine);

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
        var task = new CheckConsistencyTask(_services)
        {
            BuildId = CurrentBuildId,
            AskUser = async (question, _) =>
                Window is { } window &&
                await new Views.MessageWindow("Внимание", question, hasCancel: true).ShowDialog(window),
        };
        await ExecuteTask(task, ct => task.Execute(null, ct));
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

    public void Dispose() => _services.Dispose();
}
