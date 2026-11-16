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
/// Main window state.
/// The update button is a state machine with the same statuses and texts as

/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly LauncherServices _services;
    private readonly Mfr.Core.V2.LauncherServicesV2? _v2;
    private readonly SemaphoreSlim _taskLock = new(1, 1);

    /// <summary>К-1.2: задача идёт — кнопка показывает её ход, события опроса
    /// (связь, статусы, регион) не возвращают ей действие посреди задачи.</summary>
    private volatile bool _taskRunning;

    /// <summary>Поколение задачи: события умершей задачи не управляют кнопкой.</summary>
    private int _taskGeneration;

    /// <summary>Исполнение текущей задачи: «Продолжить» дожидается её полного
    /// снятия, прежде чем стартовать новую — иначе поколения смешиваются.</summary>
    private Task? _runningTask;
    private CancellationTokenSource? _taskCancellation;
    private UpdateStatus _lastTaskKind;

    // БТ-3/БТ-1.2: «Продолжить» возвращает к проверке целостности, только пока
    // она действительно на паузе — флаг ставится отменой сверки и снимается её
    // запуском или стартом любой другой задачи
    private bool _consistencyPaused;

    // БТ-3/пауза: экземпляр задачи сверки живёт через паузу — свёрное до паузы
    // не пересчитывается; новое «Исправить» начинает проверку с чистого листа
    private Mfr.Core.V2.CheckConsistencyV2Task? _pendingConsistency;

    /// <param name="tracePath">изоляция тестов: свой файл трейса на модель,
    /// параллельные прогоны не блокируют друг друга на одной записи.</param>
    public MainViewModel(LauncherServices? services = null, Mfr.Core.V2.LauncherServicesV2? v2 = null,
        string? tracePath = null)
    {
        _tracePath = tracePath;
        // запасовой путь сборки сервисов тоже получает настоящую платформенную реализацию
        _services = services ?? new LauncherServices(processRunner: PlatformServices.ProcessRunner);
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
        NEW_LINE,
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

    public string OnlineModeText => OnlineMode ? "Online-режим" : "Offline-режим";

    [ObservableProperty]
    private bool _updateButtonBranch;

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<string> _lines = [];

    [ObservableProperty]
    private string? _selectedLine;

    /// <summary>Индексная привязка комбобокса: SelectedItem в Avalonia теряет
    /// визуальный выбор при пересборке ItemsSource — индекс не теряет.</summary>
    [ObservableProperty]
    private int _selectedLineIndex = -1;

    partial void OnSelectedLineIndexChanged(int value)
    {
        if (!_syncingLine && value >= 0 && value < Lines.Count && Lines[value] is { } line)
        {
            SelectedLine = line; // пойдёт через обычную логику смены линии
        }
    }

    [ObservableProperty]
    private bool _linesEnabled;

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


    private bool _syncingRegion;

    partial void OnIsRuRegionChanged(bool value)
    {
        if (value && _v2 is { } v2 && !_syncingRegion)
        {
            if (_v2Lifecycle is { } lifecycle)
            {
                _ = lifecycle.ChangeRegionAsync(Mfr.Protocol.Dto.Region.RU);
            }
            else
            {
                // БТ-10.4: сервер ещё не отвечал — выбор сохраняется и применяется
                // следующей пробой, а не теряется
                v2.Region = Mfr.Protocol.Dto.Region.RU;
                _services.Repository.SetProperty(Mfr.Core.Storage.PropertyKeys.Location, "RU");
            }
        }
    }

    partial void OnIsEuRegionChanged(bool value)
    {
        if (value && _v2 is { } v2 && !_syncingRegion)
        {
            if (_v2Lifecycle is { } lifecycle)
            {
                _ = lifecycle.ChangeRegionAsync(Mfr.Protocol.Dto.Region.EU);
            }
            else
            {
                // БТ-10.4: сервер ещё не отвечал — выбор сохраняется и применяется
                // следующей пробой, а не теряется
                v2.Region = Mfr.Protocol.Dto.Region.EU;
                _services.Repository.SetProperty(Mfr.Core.Storage.PropertyKeys.Location, "EU");
            }
        }
    }

    partial void OnMinimizeToTrayChanged(bool value) =>
        _services.Repository.SetProperty(PropertyKeys.MinimizeToTray, value ? "true" : "false");

    partial void OnSpeedLimitEnabledChanged(bool value) => ApplySpeedLimit();

    partial void OnSpeedLimitKbTextChanged(string value) => ApplySpeedLimit(); // уже был: лимит переприменяется на ввод числа

    private void ApplySpeedLimit()
    {
        Trace($"speed limit: {(SpeedLimitEnabled ? SpeedLimitKbText + " КБ/с" : "выкл")}");
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
        // БТ-1.5/БТ-3.3: доступность действий определяется признаком установки,
        // а не наличием exe — прерванная на 95% установка не выглядит «игрой»
        var installed = _services.Repository.GetProperty(PropertyKeys.GameInstalled) is not null ||
                        _services.Repository.GetProperty(PropertyKeys.NewReleaseInstalled) is not null;
        ConsistencyEnabled = installed;
        GameSettingEnabled = installed;
        ClassicEnabled = installed && System.IO.File.Exists(_services.Paths.ClassicApplication);
        OpenMwEnabled = installed && System.IO.File.Exists(_services.Paths.OpenMwApplication);
    }

    partial void OnStatusChanged(UpdateStatus value)
    {
        UpdateButtonBranch = value == UpdateStatus.NEW_LINE;
        (UpdateButtonText, UpdateButtonLocked, UpdateButtonLava, HeartUpdate) = value switch
        {
            UpdateStatus.DISABLE => ("Обновлений нет", true, false, false),
            UpdateStatus.BLOCK => ("Подождите...", true, false, false),
            UpdateStatus.PAUSE => ("Приостановить", false, false, false),
            UpdateStatus.RESUME => ("Продолжить", false, false, false),
            UpdateStatus.GAME_UPDATE => ("Обновить игру", false, true, true),
            UpdateStatus.GAME_INSTALL => ("Установить игру", false, true, false),
            UpdateStatus.LAUNCHER_UPDATE => ("Обновить лаунчер", false, true, false),
            UpdateStatus.NEW_LINE => ("Новая линия игры", false, false, true),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };
    }

    /// <summary>
    /// Startup state detection (a simplified analogue of InitApplicationInitiator;
    /// the polling timer lands in phase 5): launcher update > missing game >
    /// changed build > nothing to do. When the server answers /v2,
    /// the v2 lifecycle owns the statuses instead.
    /// </summary>
    public async Task InitializeAsync()
    {
        LoadSettings();
        // БТ-11/БТ-10: офлайн-режим — осознанный отказ от сети: проба не уходит,
        // серверные действия не предлагаются; игра, если установлена, доступна
        if (!OnlineMode)
        {
            ClassicEnabled = System.IO.File.Exists(_services.Paths.ClassicApplication);
            OpenMwEnabled = System.IO.File.Exists(_services.Paths.OpenMwApplication);
            // офлайн: серверных линий нет, но сохранённую показываем выброшанной
            // и заблокированной — игрок видит, куда встанет установка
            var savedLine = _services.Repository.GetProperty(Mfr.Core.Storage.PropertyKeys.SelectedBuild);
            var known = _services.Repository.GetProperty(Mfr.Core.Storage.PropertyKeys.KnownBuilds);
            Lines.Clear();
            if (savedLine is not null)
            {
                Lines.Add(savedLine);
            }
            if (known is not null)
            {
                foreach (var line in known.Split(',').Where(l => l != savedLine))
                {
                    Lines.Add(line);
                }
            }
            _syncingLine = true;
            SelectedLine = savedLine;
            _syncingLine = false;
            LinesEnabled = false;
            Status = UpdateStatus.DISABLE;
            Description = "";
            return;
        }
        EnsurePollingLoop();
        await TryActivateV2Async().ConfigureAwait(true);
        if (_v2Active)
        {
            return; // v2 lifecycle drives the UI state from here on
        }
        // проба не прошла: сервер недоступен — клиент ждёт связи и повторяет пробу,
        // протокол вне контракта не используется (БТ-10.3, БТ-12)
        if (System.IO.File.Exists(_services.Paths.ClassicApplication))
        {
            // установленная игра остаётся доступной
            ClassicEnabled = true;
            OpenMwEnabled = System.IO.File.Exists(_services.Paths.OpenMwApplication);
            ConsistencyEnabled = false;
        }
        Status = UpdateStatus.BLOCK;
        Description = "Сервер недоступен, повторяем подключение";
        return;
#pragma warning disable CS0162 // недостижимая ветка ниже оставлена до полной вычистки легаси-пути
        if (false)
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

    // ── v2 activation: probe once, then the v2 lifecycle owns the state ──

    private bool _v2Active;
    private Mfr.Core.V2.V2LifecycleService? _v2Lifecycle;

    internal Mfr.Core.V2.LauncherServicesV2? V2Services => _v2;
    internal bool V2Active => _v2Active;

    private async Task<bool> TryActivateV2Async()
    {
        if (_v2 is not { } v2)
        {
            return _v2Active;
        }
        if (_v2Active)
        {
            return true;
        }
        IReadOnlyList<string> channels;
        try
        {
            // БТ-10.4: регион — сохранённый выбор или замер /ping — разрешается до пробы:
            // первый запрос уходит в выбранный регион, а не всегда в RU
            await Mfr.Core.V2.V2LifecycleService.ResolveRegionAsync(v2, CancellationToken.None).ConfigureAwait(false);
            // a failed probe against the v1 production resolves fast (unknown hosts)
            channels = await v2.Api.GetChannels(v2.Region).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Trace("v2 probe: server unreachable, will retry on next pass");
            return false; // сервер недоступен — повтор по следующей инициализации
        }

        // БТ-8.2: линия вычисляется до первого запроса версии и до любой задачи.
        // Неразборчивое сохранённое значение заменяет линия установленной игры,
        // а без установленной игры — первая линия перечня сервера.
        var repository = _services.Repository;
        var savedLine = repository.GetProperty(Mfr.Core.Storage.PropertyKeys.SelectedBuild);
        var savedParses = Mfr.Core.V2.VersionLine.IsLine(savedLine);
        if (!savedParses)
        {
            var installedLine = Mfr.Core.V2.V2Manifests.TryLoadLocalSchema(_services)?.Version is { } installedVersion
                ? Mfr.Core.V2.VersionLine.Of(installedVersion)
                : null;
            var effective = installedLine ?? channels.FirstOrDefault();
            if (effective is not null)
            {
                repository.SetProperty(Mfr.Core.Storage.PropertyKeys.SelectedBuild, effective);
                Trace($"v2 line resolved: saved='{savedLine}' -> '{effective}'");
            }
        }
        if (channels.Count == 0)
        {
            // БТ-8.2: перечня нет — задачи, которым нужна линия, не запускаются
            Trace("v2 activation: server returned no channels, offering nothing");
        }

        _v2Active = true;
        var lifecycle = new Mfr.Core.V2.V2LifecycleService(v2);
        _v2Lifecycle = lifecycle;
        lifecycle.SetOnlineMode(OnlineMode);

        lifecycle.GameUpdateStatusChanged += status =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_v2Active)
                {
                    return;
                }
                GameVersion = status?.CurrentVersion ?? GameVersion;
                RefreshV2Status();
            });
        lifecycle.NewLineAvailableChanged += _ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_v2Active)
                {
                    return;
                }
                RefreshV2Status();
            });
        lifecycle.AvailableBuildsChanged += builds =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_v2Active)
                {
                    return;
                }
                RebuildLines(builds);
                SyncSelectedLine();
                LinesEnabled = lifecycle.ServerConnection; // БТ-8.4: выбор линии доступен и до установки
            });
        lifecycle.LineAutoChanged += change =>
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                if (!_v2Active)
                {
                    return;
                }
                RebuildLines(lifecycle.AvailableBuilds);
                SyncSelectedLine();
                if (change.GameInstalled && Window is { } window)
                {
                    // К-2: предупредить о смене линии и несовместимости сохранений.
                    // Окно может быть скрыто в трей — диалогу нужен видимый владелец,
                    // иначе ShowDialog бросает исключение
                    window.Show();
                    await new Views.MessageWindow("Смена линии игры",
                        $"Линия {change.OldLine} больше не выпускается сервером. Клиент переведён на линию {change.NewLine}\n" +
                        "Обновите игру главной кнопкой. Сохранения другой версии могут оказаться несовместимы.").ShowDialog(window);
                }
            });
        lifecycle.GameInstalledChanged += installed =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_v2Active)
                {
                    return;
                }
                // признак установки управляет доступностью, а не наличие exe (БТ-3.3, БТ-6)
                ConsistencyEnabled = installed;
                GameSettingEnabled = installed;
                ClassicEnabled = installed && System.IO.File.Exists(_services.Paths.ClassicApplication);
                OpenMwEnabled = installed && System.IO.File.Exists(_services.Paths.OpenMwApplication);
            });
        lifecycle.LauncherUpdateAvailableChanged += _ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_v2Active)
                {
                    RefreshV2Status();
                }
            });
        lifecycle.ServerConnectionChanged += online =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_v2Active)
                {
                    return;
                }
                LinesEnabled = online;
                if (_taskRunning)
                {
                    return; // К-1.2: ход задачи важнее состояния связи
                }
                // БТ-10.3: недоступный сервер виден как недоступный, а не «Обновлений нет»
                Description = online ? "" : "Сервер недоступен, повторяем подключение";
                if (!online)
                {
                    Status = UpdateStatus.BLOCK;
                }
                else
                {
                    // восстановление связи: статусы перезапрашиваются сразу (БТ-10.3)
                    _ = _v2Lifecycle?.RefreshStatusesAsync();
                    RefreshV2Status();
                }
            });
        lifecycle.GameVersionChanged += version =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => GameVersion = version);
        lifecycle.LocationChanged += region =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _syncingRegion = true;
                IsEuRegion = region == Mfr.Protocol.Dto.Region.EU;
                IsRuRegion = !IsEuRegion;
                _syncingRegion = false;
            });

        // радиокнопки отражают регион после его загрузки/замера (БТ-10: UI = факт)
        _syncingRegion = true;
        IsEuRegion = v2.Region == Mfr.Protocol.Dto.Region.EU;
        IsRuRegion = !IsEuRegion;
        _syncingRegion = false;

        Status = UpdateStatus.BLOCK;
        try
        {
            await lifecycle.InitializeAsync().ConfigureAwait(false);
            // после загрузки сохранённого региона/замера — синхронизировать радиокнопки
            _syncingRegion = true;
            IsEuRegion = v2.Region == Mfr.Protocol.Dto.Region.EU;
            IsRuRegion = !IsEuRegion;
            _syncingRegion = false;
        }
        catch (Exception exception)
        {
            Trace($"v2 lifecycle init failed: {exception.Message}");
            // lifecycle failures keep the last known state; polling will recover
        }

        // страховка: линия всё ещё пуста — берём первую из перечня (самая свежая)
        if (_services.Repository.GetProperty(Mfr.Core.Storage.PropertyKeys.SelectedBuild) is null &&
            lifecycle.AvailableBuilds is { Count: > 0 } firstChannels)
        {
            _services.Repository.SetProperty(Mfr.Core.Storage.PropertyKeys.SelectedBuild, firstChannels[0]);
        }

        // v1→v2 with an existing game but no proto schema yet: re-detect applied
        // options from the installed files once (fills sections + defaults)
        if (lifecycle.InstalledSchema is null &&
            System.IO.File.Exists(_services.Paths.ClassicApplication))
        {
            try
            {
                var fill = new Mfr.Core.V2.FillSchemaV2Task(v2);
                await fill.Execute(null, CancellationToken.None).ConfigureAwait(true);
                await lifecycle.InitializeAsync().ConfigureAwait(false);
                Trace($"v2 fill-schema: restored options from files, schema={lifecycle.InstalledSchema?.Version ?? "none"}");
            }
            catch (Exception exception)
            {
                Trace($"v2 fill-schema failed: {exception.Message}");
            }
        }

        RefreshV2Status();
        LinesEnabled = lifecycle.ServerConnection;
        Trace($"v2 active: channels=[{string.Join(",", lifecycle.AvailableBuilds)}] " +
              $"schema={lifecycle.InstalledSchema?.Version ?? "none"} status={Status}");
        return true;
    }

    /// <summary>dev refreshStatus priority: launcher update &gt; install &gt; game update &gt; new line &gt; idle.</summary>
    internal void RefreshV2Status()
    {
        if (_v2Lifecycle is not { } lifecycle)
        {
            return;
        }
        // К-1.2: во время задачи статус принадлежит ходу задачи (Блок/Пауза),
        // а не опросу сервера — кнопка не предлагает действие, пока задача идёт
        if (_taskRunning)
        {
            return;
        }
        // БТ-11/БТ-10: в офлайне серверных действий нет — установка и обновление
        // невозможны без сети, кнопка ничего не предлагает
        if (!OnlineMode)
        {
            HeartUpdate = false;
            Status = UpdateStatus.DISABLE;
            return;
        }
        HeartUpdate = lifecycle.NewLineAvailable is not null || lifecycle.GameUpdate is { NeedUpdate: true };
        if (lifecycle.LauncherUpdateAvailable)
        {
            Status = UpdateStatus.LAUNCHER_UPDATE; // К-1: обновление клиента старше всего
            return;
        }
        if (!lifecycle.GameInstalled)
        {
            // К-3: пока в линии нет выпуска, установка не предлагается
            Status = lifecycle.LineHasNothing ? UpdateStatus.DISABLE : UpdateStatus.GAME_INSTALL;
        }
        else if (lifecycle.GameUpdate is { NeedUpdate: true })
        {
            Status = UpdateStatus.GAME_UPDATE;
        }
        else if (lifecycle.NewLineAvailable is not null)
        {
            Status = UpdateStatus.NEW_LINE;
        }
        else
        {
            Status = UpdateStatus.DISABLE;
        }
        SyncSelectedLine();
    }

    /// <summary>К-2: текущая линия видна в списке, даже если её уже нет в перечне сервера.</summary>
    private void RebuildLines(IReadOnlyList<string> channels)
    {
        // отложенные посты диспетчера могут сработать после закрытия клиента —
        // репозиторий уже освобождён: это штатное завершение, а не ошибка
        try
        {
        Lines.Clear();
        var current = _services.Repository.GetProperty(Mfr.Core.Storage.PropertyKeys.SelectedBuild);
        if (current is not null && !channels.Contains(current))
        {
            Lines.Add(current);
        }
        foreach (var channel in channels)
        {
            Lines.Add(channel);
        }
        // пересборка сбрасывает выбранный элемент комбобокса; повторное
        // уведомление нужно ПОСЛЕ того, как комбобокс обработает новую
        // коллекцию — поэтому перевыбор отложенным постом
        Avalonia.Threading.Dispatcher.UIThread.Post(SyncSelectedLine);
        }
        catch (ObjectDisposedException)
        {
            return; // клиент закрывается
        }
    }

    private bool _syncingLine;

    private void SyncSelectedLine()
    {
        if (_v2Lifecycle is null)
        {
            return;
        }
        var current = _services.Repository.GetProperty(Mfr.Core.Storage.PropertyKeys.SelectedBuild);
        Trace($"line sync: repo='{current}' ui='{SelectedLine}' items=[{string.Join(",", Lines)}]");
        _syncingLine = true;
        SelectedLineIndex = current is null ? -1 : Lines.IndexOf(current);
        _syncingLine = false;
        if (!string.Equals(SelectedLine, current, StringComparison.Ordinal))
        {
            _syncingLine = true;
            SelectedLine = current;
            _syncingLine = false;
        }
    }

    partial void OnSelectedLineChanged(string? value)
    {
        if (_syncingLine || !_v2Active || value is null)
        {
            return;
        }
        if (_v2Lifecycle is { GameInstalled: false })
        {
            // БТ-8.4: до установки выбор сохраняется молча, установка пойдёт в выбранную линию
            _services.Repository.SetProperty(Mfr.Core.Storage.PropertyKeys.SelectedBuild, value);
            return;
        }
        _ = ConfirmLineSwitchAsync(value);
    }

    /// <summary>dev askLineSwitch: agree selects the line and updates, refuse hides it until the next one.</summary>
    private async Task ConfirmLineSwitchAsync(string line)
    {
        var current = _services.Repository.GetProperty(Mfr.Core.Storage.PropertyKeys.SelectedBuild);
        var description =
            $"Глобальная версия игры {line}" +
            (current is null ? "" : $" (сейчас установлена {current})") + "\n" +
            "Переход скачает изменившиеся файлы игры. Сохранения другой версии могут оказаться несовместимы.\n" +
            "Перейти сейчас?";
        var agreed = Window is { } window &&
                     await new Views.MessageWindow("Глобальная версия игры", description, hasCancel: true).ShowDialog(window);
        if (agreed)
        {
            _services.Repository.SetProperty(Mfr.Core.Storage.PropertyKeys.SelectedBuild, line);
            _v2Lifecycle?.DismissNewLine();
            _lastTaskKind = UpdateStatus.GAME_UPDATE;
            await RestartLastTask(freshIntent: true);
        }
        else
        {
            // БТ-8.3: отказ запоминается до появления следующей линии
            _v2Lifecycle?.DismissNewLine();
            SyncSelectedLine(); // revert the combo
        }
    }

    private bool _pollingStarted;

    /// <summary>Опрос раз в 5 минут; запускается один раз на процесс — повторные
    /// вызовы инициализации циклов не плодят (БТ-11: рост нагрузки в фоне).</summary>
    private void EnsurePollingLoop()
    {
        if (_pollingStarted)
        {
            return;
        }
        _pollingStarted = true;
        _ = RunPollingLoop();
    }

    private async Task RunPollingLoop()
    {
        // БТ-10.3: ожидание рубится на короткие такты — переход в BLOCK посреди
        // пятиминутного ожидания не должен откладывать пробу ещё на пять минут
        var tick = TimeSpan.FromSeconds(20);
        var waited = TimeSpan.Zero;
        while (true)
        {
            await Task.Delay(tick);
            waited += tick;
            if (waited < PollDelay(Status))
            {
                continue;
            }
            waited = TimeSpan.Zero;
            if (!OnlineMode)
            {
                continue; // offline mode: no server polling
            }
            if (!await _taskLock.WaitAsync(0))
            {
                continue; // a task is running; its completion re-detects the state anyway
            }
            _taskLock.Release();
            // БТ-10.3: BLOCK — сервер был недоступен на старте — тоже повторяется
            // опросом: проба перезапускается без перезапуска клиента
            if (Status is UpdateStatus.BLOCK or UpdateStatus.GAME_INSTALL or UpdateStatus.LAUNCHER_UPDATE
                or UpdateStatus.GAME_UPDATE or UpdateStatus.DISABLE)
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

    /// <summary>
    /// БТ-10.3: в BLOCK сервер пробуется часто — «повторяем подключение» не может
    /// означать «раз в пять минут», игрок должен видеть оживление в течение
    /// полуминуты; штатный опрос остаётся редким.
    /// </summary>
    internal static TimeSpan PollDelay(UpdateStatus status) =>
        status == UpdateStatus.BLOCK ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(5);

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

            // К-1/БТ-3.6: явные действия кнопки делают то, что на ней написано,
            // даже когда где-то живёт пауза сверки
            case UpdateStatus.GAME_UPDATE:
                _lastTaskKind = UpdateStatus.GAME_UPDATE;
                await RestartLastTask(freshIntent: true);
                break;

            case UpdateStatus.GAME_INSTALL:
                _lastTaskKind = UpdateStatus.GAME_INSTALL;
                await RestartLastTask(freshIntent: true);
                break;

            case UpdateStatus.LAUNCHER_UPDATE:
                _lastTaskKind = UpdateStatus.LAUNCHER_UPDATE;
                await RestartLastTask(freshIntent: true);
                break;

            case UpdateStatus.NEW_LINE:
                if (_v2Lifecycle?.NewLineAvailable is { } line)
                {
                    await ConfirmLineSwitchAsync(line);
                }
                break;
        }
    }

    internal async Task RestartLastTask(bool freshIntent = false)
    {
        // БТ-3/БТ-1.2: маршрут возобновления решает ядро — проверка целостности
        // тоже задача, её пауза возобновляется самой проверкой
        // прежняя задача ещё разматывается после паузы — ждём её ПОЛНОГО снятия:
        // замок освобождается раньше, чем хвосты задачи, и новый запуск смешивал
        // поколения: перезаписанный токен, припоздавшие события, гонки на .part
        if (_runningTask is { IsCompleted: false } previous)
        {
            Trace("resume: waiting previous task unwind");
            await previous.ConfigureAwait(true);
        }
        var lastKind = _lastTaskKind switch
        {
            UpdateStatus.GAME_UPDATE => Mfr.Core.Tasks.LauncherTaskKind.GameUpdate,
            UpdateStatus.GAME_INSTALL => Mfr.Core.Tasks.LauncherTaskKind.GameInstall,
            UpdateStatus.LAUNCHER_UPDATE => Mfr.Core.Tasks.LauncherTaskKind.LauncherUpdate,
            _ => Mfr.Core.Tasks.LauncherTaskKind.None,
        };
        if (Mfr.Core.Tasks.ResumeRouter.Route(lastKind, !freshIntent && _consistencyPaused)
            == Mfr.Core.Tasks.ResumeRoute.Consistency)
        {
            await RunConsistency(resumePaused: true);
            return;
        }
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
                case UpdateStatus.LAUNCHER_UPDATE:
                    await ExecuteTask(new Mfr.Core.V2.LauncherUpdateV2Task(v2), (t, ct) => t.Execute(null, ct), UpdateStatus.LAUNCHER_UPDATE);
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

    /// <summary>БТ-3.6: задача окна опций — тоже «любая другая задача»: пауза сверки
    /// гаснет, следующая проверка начнётся с чистого листа.</summary>
    internal void ExternalTaskStarted()
    {
        _consistencyPaused = false;
        _pendingConsistency = null;
    }

    private Task ExecuteTask<TParam>(LauncherTask<TParam, object?> task, Func<LauncherTask<TParam, object?>, CancellationToken, Task> run, UpdateStatus kind)
    {
        _lastTaskKind = kind;
        _consistencyPaused = false; // игровая или лаунчерная задача сменила маршрут возобновления
        _pendingConsistency = null; // и сняла ожидающую возобновления проверку
        return ExecuteTask(task, ct => run(task, ct));
    }

    internal async Task ExecuteTask(LauncherTask task, Func<CancellationToken, Task> run)
    {
        if (!await _taskLock.WaitAsync(0))
        {
            return;
        }
        Trace($"task {task.GetType().Name} start");
        _taskRunning = true;
        var generation = Interlocked.Increment(ref _taskGeneration);
        _taskCancellation = new CancellationTokenSource();
        Status = UpdateStatus.BLOCK;
        Progress = ProgressState.Empty;
        Percent = 0;
        ShowSettings = false; // switch to the main screen so the progress bar is visible
        Action<int> onProgress = percent => OnTaskProgress(percent, generation);
        Action<string> onDescription = description => OnTaskDescription(description, generation);
        task.ProgressChanged += onProgress;
        task.DescriptionChanged += onDescription;
        var taskSucceeded = false;
        _runningTask = RunTaskAsync();
        try
        {
            await _runningTask;
            Trace($"task {task.GetType().Name} finished");
            taskSucceeded = true;
            Progress = ProgressState.Full;
            Percent = 100;
            Description = ""; // статусные строки не остаются висеть после задачи
        }
        catch (OperationCanceledException oce)
        {
            Trace($"task cancelled: {oce.Message}");
            // БТ-10.1: таймаут HttpClient — это TaskCanceledException с TimeoutException
            // внутри; это ошибка с повтором, а не пауза игрока
            var httpTimeout = oce is TaskCanceledException { InnerException: TimeoutException };
            if (!httpTimeout && task is Mfr.Core.V2.CheckConsistencyV2Task or CheckConsistencyTask)
            {
                // БТ-3/БТ-1.2: пауза коснулась именно сверки — «Продолжить» вернётся к ней
                _consistencyPaused = true;
            }
            Progress = ProgressState.Hidden; // paused by the user
            if (!OnlineMode)
            {
                // задачу снял переход в офлайн: опрос молчит, кнопку гасит здесь сам выход
                Status = UpdateStatus.DISABLE;
                return;
            }
            if (!httpTimeout)
            {
                return; // обычная пауза игрока: статус восстановит «Продолжить»
            }
            // БТ-10.1: таймаут — ошибка связи, а не пауза: без пересчёта кнопка
            // навсегда остаётся «Подождите…» (BLOCK опросом не лечится при активном v2)
            if (_v2Lifecycle is { } lifecycle)
            {
                // в фоне: на UI-потоке ожидание повторов сети заморозило бы окно;
                // завершение поднимет события статусов, они и обновят кнопку
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await lifecycle.RefreshStatusesAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // следующий тик опроса повторит
                    }
                });
                RefreshV2Status();
            }
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
            Interlocked.Increment(ref _taskGeneration); // события задачи после этого — припоздавшие
            _taskRunning = false; // сначала снимаем флаг: пересчёт статуса ниже по ходу
            task.ProgressChanged -= onProgress;
            task.DescriptionChanged -= onDescription;
            _taskCancellation.Dispose();
            _taskCancellation = null;
            _taskLock.Release();
        }

        async Task RunTaskAsync() => await run(_taskCancellation!.Token);

        if (_v2Active && _v2Lifecycle is { } activeLifecycle)
        {
            if (taskSucceeded)
            {
                activeLifecycle.OnGameInstalled();
                RefreshV2Status();
            }
            else
            {
                // К-1: после неудачи кнопка снова предлагает то же действие;
                // RefreshStatusesAsync — в фоне: сетевые повторы не должны морозить окно
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await activeLifecycle.RefreshStatusesAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                });
                RefreshV2Status();
            }
        }
        else if (taskSucceeded)
        {
            await InitializeAsync();
            GameVersion = _services.Paths.GameVersion;
        }
    }

    // progress events come from worker tasks; marshal to the UI thread,
    // throttled to ~10 updates/sec so the UI thread stays responsive to clicks

    // written next to the exe, not the process working directory (which varies with launch method);
    // трейс идёт из нескольких потоков (задачи, опрос) — запись сериализована
    private static readonly object TraceGate = new();

    private readonly string? _tracePath;

    private string TracePath => _tracePath ??
        System.IO.Path.Combine(AppContext.BaseDirectory, "task-trace.log");

    private void Trace(string message)
    {
        lock (TraceGate)
        {
            RotateIfLarge(TracePath);
            System.IO.File.AppendAllText(TracePath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}" + Environment.NewLine);
        }
    }

    /// <summary>Keep diagnostic logs bounded: past 1 MB keep the newest half.</summary>
    private static void RotateIfLarge(string path)
    {
        try
        {
            var info = new System.IO.FileInfo(path);
            if (!info.Exists || info.Length < 1_000_000)
            {
                return;
            }
            var lines = System.IO.File.ReadAllLines(path);
            System.IO.File.WriteAllLines(path, lines[(lines.Length / 2)..]);
        }
        catch (Exception)
        {
            // rotation must never break the flow it diagnoses
        }
    }

    private long _lastProgressPost;

    private void OnTaskProgress(int percent, int generation)
    {
        if (generation != Volatile.Read(ref _taskGeneration))
        {
            return; // задача умерла: её припоздавший прогресс не возвращает кнопке действие
        }
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

    private void OnTaskDescription(string description, int generation) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (generation == Volatile.Read(ref _taskGeneration))
            {
                Description = description;
                Trace($"stage: {description}"); // хронология стадий: по ней видно, где задача стоит
            }
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
    private Task CheckConsistency() => RunConsistency(resumePaused: false);

    /// <summary>БТ-3.6: новое «Исправить» начинает проверку с чистого листа;
    /// возобновление после паузы возвращается к живому экземпляру со свёрнутым.</summary>
    private async Task RunConsistency(bool resumePaused)
    {
        Trace($"CheckConsistency invoked: resume={resumePaused}");
        if (!ConsistencyEnabled)
        {
            Trace("CheckConsistency skipped: button disabled");
        }
        _consistencyPaused = false; // запуск проверки: прежняя пауза (если была) погашена
        // the task body runs on a worker thread; windows must be created on the UI thread
        System.Func<string, System.Threading.CancellationToken, Task<bool>> askUser = async (question, _) =>
            Window is { } window &&
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => new Views.MessageWindow("Внимание", question, hasCancel: true).ShowDialog(window));
        if (_v2Active && _v2 is { } v2)
        {
            // БТ-3/пауза: возобновление возвращается к тому же экземпляру задачи —
            // он помнит свёрное до паузы и не пересчитывает его (как загрузка, БТ-1.2)
            var task = resumePaused
                ? _pendingConsistency ?? new Mfr.Core.V2.CheckConsistencyV2Task(v2) { AskUser = askUser }
                : new Mfr.Core.V2.CheckConsistencyV2Task(v2) { AskUser = askUser };
            _pendingConsistency = task;
            await ExecuteTask(task, ct => task.Execute(null, ct));
            if (Status != UpdateStatus.RESUME)
            {
                _pendingConsistency = null; // не пауза — проверка завершена, следующая начнётся заново
            }
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
    private void OpenSupportLog()
    {
        // БТ-12: журнал для передачи в поддержку — файл выделен в проводнике
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{System.IO.Path.Combine(AppContext.BaseDirectory, "task-trace.log")}\"",
            UseShellExecute = true,
        });
    }

    partial void OnOnlineModeChanged(bool value) => OnPropertyChanged(nameof(OnlineModeText));

    [RelayCommand]
    private async Task SwitchOnlineModeAsync()
    {
        OnlineMode = !OnlineMode;
        _services.Repository.SetProperty(PropertyKeys.OnlineMode, OnlineMode ? "true" : "false");
        _v2Lifecycle?.SetOnlineMode(OnlineMode);
        if (!OnlineMode)
        {
            LinesEnabled = false;
            // офлайн — отказ от сети: идущая загрузка снимается, как паузой;
            // повтор доступен после возврата в online
            _taskCancellation?.Cancel();
            if (!_taskRunning)
            {
                Description = ""; // «сервер недоступен»/ход снятой задачи не висит под погасшей кнопкой
            }
            RefreshV2Status(); // БТ-11: кнопка сразу перестаёт предлагать сетевые действия
            return;
        }
        // БТ-10.3/БТ-11: возврат в online подключается сразу, без перезапуска клиента.
        // После офлайн-старта опрос ещё не запущен — запускаем: он и есть повтор
        EnsurePollingLoop();
        if (_v2Lifecycle is { } lifecycle)
        {
            await lifecycle.ChangeRegionAsync(lifecycle.Region).ConfigureAwait(true);
        }
        else if (!await TryActivateV2Async().ConfigureAwait(true))
        {
            // сервер недоступен: честный BLOCK с частой пробой, а не «Обновлений нет»
            Status = UpdateStatus.BLOCK;
            Description = "Сервер недоступен, повторяем подключение";
            return;
        }
        RefreshV2Status();
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
    private void OpenReadme()
    {
        // БТ-7: руководство — с сайта, а без сети — из папки игры
        if (OnlineMode)
        {
            OpenLink(Mfr.Core.Services.CommunityLinks.ReadmeRemote);
            return;
        }
        var local = _services.Paths.Resolve(Mfr.Core.Services.CommunityLinks.ReadmeLocalPath);
        if (File.Exists(local))
        {
            OpenLink(local);
        }
        // офлайн без локальной копии: сети нет по решению игрока, удалённую версию
        // не открываем — локальная появится после установки сборки
    }

    [RelayCommand]
    private void OpenFaq()
    {
        // БТ-7: FAQ открывается сайтом, без сети — локальной копией из сборки
        if (OnlineMode)
        {
            OpenLink(Mfr.Core.Services.CommunityLinks.FaqRemote);
            return;
        }
        var local = _services.Paths.Resolve(Mfr.Core.Services.CommunityLinks.FaqLocalPath);
        if (File.Exists(local))
        {
            OpenLink(local);
        }
        // офлайн без локальной копии: удалённую версию не открываем
    }

    [RelayCommand]
    private void OpenForum() => OpenLink(Mfr.Core.Services.CommunityLinks.Forum);

    [RelayCommand]
    private void OpenDonation()
    {
        if (Window is { } w) { new Views.DonationWindow().Show(w); }
    }

    [RelayCommand]
    private void OpenDiscord() => OpenLink(Mfr.Core.Services.CommunityLinks.Discord);

    [RelayCommand]
    private void OpenYoutube() => OpenLink(Mfr.Core.Services.CommunityLinks.YouTube);

    [RelayCommand]
    private void OpenVk() => OpenLink(Mfr.Core.Services.CommunityLinks.Vk);

    [RelayCommand]
    private void OpenPatreon() => OpenLink("https://www.patreon.com/aLMFR");


    [RelayCommand]
    private void OpenTelegram() => OpenLink(Mfr.Core.Services.CommunityLinks.Telegram);

    [RelayCommand]
    private void OpenWebsite() => OpenLink(Mfr.Core.Services.CommunityLinks.Website);

    private static void OpenLink(string url) =>
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

    public void Dispose()
    {
        _v2Lifecycle?.Dispose();
        _services.Dispose();
    }
}
