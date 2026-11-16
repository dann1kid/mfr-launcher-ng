using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mfr.Core.Services;

namespace Mfr.Launcher.Views;

/// <summary>
/// Graphics preset picker used for both MGE (with a "Старт MGE" button) and
/// OpenMW. Detects the current preset
/// by checksums and backs the config up before switching.
/// </summary>
public partial class PresetWindow : Window
{
    public PresetWindow(string header, Func<MgeConfiguration> detectMge, Action<MgeConfiguration, bool> applyMge,
        Func<OpenMwConfiguration?> detectOpenMw, Action<OpenMwConfiguration, bool> applyOpenMw,
        bool mgeMode, Action? onExtra = null, Func<System.Diagnostics.Process?>? onExtraProcess = null,
        Func<bool>? detectBackup = null, Func<bool>? configExists = null)
    {
        var viewModel = new ViewModel(header, mgeMode, onExtra, Close)
        {
            DetectMge = detectMge,
            ApplyMge = applyMge,
            DetectOpenMw = detectOpenMw,
            ApplyOpenMw = applyOpenMw,
            OnExtraProcess = onExtraProcess,
            DetectBackup = detectBackup,
            ConfigExists = configExists,
        };
        viewModel.LoadCurrent();
        DataContext = viewModel;
        InitializeComponent();
        PointerPressed += OnPointerPressed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        // тащим только за шапку: прозрачные поля рамки — не ручка перетаскивания
        if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed && args.GetPosition(this).Y < 90)
        {
            BeginMoveDrag(args);
        }
    }

    internal static PresetWindow ForMge(MgeService mge, GameRunner runner) => new(
        "Пресеты MGE",
        mge.FindActiveConfig,
        (configuration, backup) => mge.ApplyConfig(configuration, backup),
        () => null,
        (_, _) => { },
        mgeMode: true,
        onExtra: () => runner.StartMge(),
        onExtraProcess: () => runner.StartMge(),
        detectBackup: mge.HasConfigBackup,
        configExists: mge.ConfigExists);

    internal static PresetWindow ForOpenMw(OpenMwService openMw) => new(
        "OpenMW",
        () => MgeConfiguration.CUSTOM,
        (_, _) => { },
        openMw.FindActiveConfig,
        (configuration, backup) => openMw.ApplyConfig(configuration, backup),
        mgeMode: false,
        detectBackup: openMw.HasConfigBackup,
        configExists: openMw.ConfigExists);

    internal sealed partial class ViewModel : ObservableObject
    {
        private readonly string _header;
        private readonly bool _mgeMode;
        private readonly Action? _onExtra;
        private readonly Action _close;
        private int _selected; // 0..4: high, middle, low, basic, custom

        internal Func<MgeConfiguration> DetectMge { get; init; } = () => MgeConfiguration.CUSTOM;
        internal Action<MgeConfiguration, bool> ApplyMge { get; init; } = (_, _) => { };
        internal Func<OpenMwConfiguration?> DetectOpenMw { get; init; } = () => null;
        internal Action<OpenMwConfiguration, bool> ApplyOpenMw { get; init; } = (_, _) => { };
        /// <summary>Есть ли бэкап собственных настроек — определяет доступность «Своих настроек».</summary>
        internal Func<bool>? DetectBackup { get; init; }
        /// <summary>Существует ли конфиг: до установки пресетов окно показывает подсказку без выбора.</summary>
        internal Func<bool>? ConfigExists { get; init; }
        private bool ConfigPresent => ConfigExists?.Invoke() ?? true;
        private bool BackupExists => DetectBackup?.Invoke() ?? false;
        /// <summary>БТ-5.3: «Свои настройки» активны, если сейчас стоят кастомные
        /// настройки или их бэкап существует — иначе возвращать нечего.</summary>
        public bool IsCustomEnabled => ConfigPresent && (_detectedCustom || BackupExists);

        /// <summary>Запуск настройщика; его закрытие перечитывает активный пресет.</summary>
        public Func<System.Diagnostics.Process?>? OnExtraProcess { get; set; }

        public ViewModel(string header, bool mgeMode, Action? onExtra, Action close)
        {
            _header = header;
            _mgeMode = mgeMode;
            _onExtra = onExtra;
            _close = close;
        }

        public void LoadCurrent()
        {
            var index = _mgeMode
                ? DetectMge() switch
                {
                    MgeConfiguration.HIGH => 0,
                    MgeConfiguration.MIDDLE => 1,
                    MgeConfiguration.LOW => 2,
                    MgeConfiguration.BASIC => 3,
                    _ => 4,
                }
                : DetectOpenMw() switch
                {
                    OpenMwConfiguration.HIGH => 0,
                    OpenMwConfiguration.MIDDLE => 1,
                    OpenMwConfiguration.LOW => 2,
                    OpenMwConfiguration.BASIC => 3,
                    _ => 4,
                };
            Selected = index;
            // БТ-5.3: судьбу копии «Своих настроек» решает конфигурация, найденная
            // при открытии окна, — не последовательность кликов после него
            _detectedCustom = ConfigPresent && index == 4;
            if (!ConfigPresent)
            {
                Selected = -1; // конфига нет — ни один пункт не выбран
            }
            OnPropertyChanged(nameof(IsCustomEnabled));
            OnPropertyChanged(nameof(Hint));
        }

        public string HeaderText => _header;

        public bool HasExtraButton => _mgeMode;

        public string ExtraButtonText => "Старт MGE";

        public string Hint => !ConfigPresent
            ? "Пресеты станут доступны после установки игры."
            : Selected == 4
                ? (BackupExists
                    ? "Активны собственные настройки. При смене пресета они сохранятся в бэкап."
                    : "Активны собственные настройки.")
                : BackupExists
                    ? "При смене пресета текущие настройки сохранятся в бэкап — вернуть можно выбором «Свои настройки»."
                    : "Активен пресет. Собственные настройки появятся после их изменения вручную.";

        private int Selected
        {
            get => _selected;
            set
            {
                if (_selected != value)
                {
                    _selected = value;
                    OnPropertyChanged(nameof(IsHigh));
                    OnPropertyChanged(nameof(IsMiddle));
                    OnPropertyChanged(nameof(IsLow));
                    OnPropertyChanged(nameof(IsBasic));
                    OnPropertyChanged(nameof(IsCustom));
                    OnPropertyChanged(nameof(Hint));
                }
            }
        }

        /// <summary>Конфигурация была «своей» на открытии окна — только это решает, делать ли копию.</summary>
        private bool _detectedCustom;

        public bool IsHigh { get => Selected == 0; set { if (value) Selected = 0; } }
        public bool IsMiddle { get => Selected == 1; set { if (value) Selected = 1; } }
        public bool IsLow { get => Selected == 2; set { if (value) Selected = 2; } }
        public bool IsBasic { get => Selected == 3; set { if (value) Selected = 3; } }
        public bool IsCustom { get => Selected == 4; set { if (value) Selected = 4; } }

        public RelayCommand ApplyCommand => new(() =>
        {
            // БТ-5.3: копия существует ради сохранения собственных настроек игрока.
            // На открытии была своя конфигурация — любой переход снимает её копию;
            // был пресет — копия не трогается, и «Свои настройки» возвращают прежнюю
            var backup = _detectedCustom;
            if (_mgeMode)
            {
                ApplyMge(ToMge(_selected), backup);
            }
            else
            {
                ApplyOpenMw(ToOpenMw(_selected), backup);
            }
            _close();
        });

        public RelayCommand ExtraCommand => new(async () =>
        {
            _onExtra?.Invoke();
            var process = OnExtraProcess?.Invoke();
            if (process is null)
            {
                return;
            }
            await process.WaitForExitAsync().ConfigureAwait(true);
            // БТ-5.3: конфигурация изменилась настройщиком — детекция перечитывается,
            // и решение о копии «Своих настроек» принимается по ней
            Avalonia.Threading.Dispatcher.UIThread.Post(LoadCurrent);
        });

        public RelayCommand CloseCommand => new(_close);

        private static MgeConfiguration ToMge(int index) => index switch
        {
            0 => MgeConfiguration.HIGH,
            1 => MgeConfiguration.MIDDLE,
            2 => MgeConfiguration.LOW,
            3 => MgeConfiguration.BASIC,
            _ => MgeConfiguration.CUSTOM,
        };

        private static OpenMwConfiguration ToOpenMw(int index) => index switch
        {
            0 => OpenMwConfiguration.HIGH,
            1 => OpenMwConfiguration.MIDDLE,
            2 => OpenMwConfiguration.LOW,
            3 => OpenMwConfiguration.BASIC,
            _ => OpenMwConfiguration.CUSTOM,
        };
    }
}
