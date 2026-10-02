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
/// OpenMW (Kotlin: MgeController/OpenMwController). Detects the current preset
/// by checksums and backs the config up before switching.
/// </summary>
public partial class PresetWindow : Window
{
    public PresetWindow(string header, Func<MgeConfiguration> detectMge, Action<MgeConfiguration, bool> applyMge,
        Func<OpenMwConfiguration?> detectOpenMw, Action<OpenMwConfiguration, bool> applyOpenMw,
        bool mgeMode, Action? onExtra = null)
    {
        var viewModel = new ViewModel(header, mgeMode, onExtra, Close)
        {
            DetectMge = detectMge,
            ApplyMge = applyMge,
            DetectOpenMw = detectOpenMw,
            ApplyOpenMw = applyOpenMw,
        };
        viewModel.LoadCurrent();
        DataContext = viewModel;
        InitializeComponent();
        PointerPressed += OnPointerPressed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
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
        onExtra: () => runner.StartMge());

    internal static PresetWindow ForOpenMw(OpenMwService openMw) => new(
        "OpenMW",
        () => MgeConfiguration.CUSTOM,
        (_, _) => { },
        openMw.FindActiveConfig,
        (configuration, backup) => openMw.ApplyConfig(configuration, backup),
        mgeMode: false);

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
        }

        public string HeaderText => _header;

        public bool HasExtraButton => _mgeMode;

        public string ExtraButtonText => "Старт MGE";

        public string Hint => _selected == 4
            ? "Текущая конфигурация не совпадает ни с одним пресетом — она будет сохранена в бэкап при смене."
            : "Текущий пресет определён по контрольным суммам конфигурационных файлов.";

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

        public bool IsHigh { get => Selected == 0; set { if (value) Selected = 0; } }
        public bool IsMiddle { get => Selected == 1; set { if (value) Selected = 1; } }
        public bool IsLow { get => Selected == 2; set { if (value) Selected = 2; } }
        public bool IsBasic { get => Selected == 3; set { if (value) Selected = 3; } }
        public bool IsCustom { get => Selected == 4; set { if (value) Selected = 4; } }

        public RelayCommand ApplyCommand => new(() =>
        {
            if (_mgeMode)
            {
                ApplyMge(ToMge(_selected), true);
            }
            else
            {
                ApplyOpenMw(ToOpenMw(_selected), true);
            }
            _close();
        });

        public RelayCommand ExtraCommand => new(() => _onExtra?.Invoke());

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
