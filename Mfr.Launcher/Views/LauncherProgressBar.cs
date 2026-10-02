using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mfr.Launcher.ViewModels;

namespace Mfr.Launcher.Views;

/// <summary>
/// The skinned progress bar (CSS classes .progress-bar .enable/.full/.empty/.disable/.hide):
/// a 340px frame, a growing track (start/middle/end PNGs), a percent ellipse
/// and a description line; the "disable" state shows the error picture.
/// </summary>
public sealed class LauncherProgressBar : UserControl
{
    public static readonly StyledProperty<int> PercentProperty =
        AvaloniaProperty.Register<LauncherProgressBar, int>(nameof(Percent));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<LauncherProgressBar, string?>(nameof(Description));

    public static readonly StyledProperty<MainViewModel.ProgressState> StateProperty =
        AvaloniaProperty.Register<LauncherProgressBar, MainViewModel.ProgressState>(nameof(State));

    private readonly Image _borderEnable;
    private readonly Image _borderFull;
    private readonly Grid _disableGroup;
    private readonly Grid _track;
    private readonly ColumnDefinition _trackMiddle;
    private readonly Grid _percentGroup;
    private readonly TextBlock _percentText;

    public int Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public MainViewModel.ProgressState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public LauncherProgressBar()
    {
        _borderEnable = BorderImage("progress_bar/bar_border_enable.png");
        _borderFull = BorderImage("progress_bar/bar_border_full.png");

        var error = new Image
        {
            Source = Load("progress_bar/error.png"),
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _disableGroup = new Grid { Children = { BorderImage("progress_bar/bar_border_disable.png"), error } };

        _trackMiddle = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Pixel) };
        _track = new Grid
        {
            Height = 20,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(20, 40, 20, 0),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(26, GridUnitType.Pixel) },
                _trackMiddle,
                new ColumnDefinition { Width = new GridLength(21, GridUnitType.Pixel) },
            },
            Children =
            {
                Segment("progress_bar/start.png", 0),
                Segment("progress_bar/middle.png", 1),
                Segment("progress_bar/end.png", 2),
            },
        };

        _percentText = new TextBlock { Classes = { "percents" } };
        _percentGroup = new Grid
        {
            Width = 41,
            Height = 42,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                new Image { Source = Load("progress_bar/progress_ellipse.png"), Stretch = Stretch.None },
                _percentText,
            },
        };

        var borderLayer = new Grid { Height = 82, Children = { _borderEnable, _borderFull, _disableGroup } };
        var cover = new Grid
        {
            Children = { borderLayer, _track, _percentGroup },
        };

        Content = new StackPanel
        {
            Width = 340,
            Children =
            {
                cover,
                new TextBlock
                {
                    Classes = { "description", "red" },
                    Text = null,
                    [!TextBlock.TextProperty] = new Binding(nameof(Description)) { Mode = BindingMode.OneWay },
                    Height = 30,
                    TextAlignment = TextAlignment.Center,
                },
            },
        };

        this[!PercentProperty] = new Binding(nameof(Percent)) { Mode = BindingMode.OneWay, Source = this };

        // apply the initial state: the changed-callback won't fire if the
        // bound value equals the property default
        OnPercentChanged(Percent);
        OnStateChanged(State);
    }

    static LauncherProgressBar()
    {
        PercentProperty.Changed.Subscribe(Observer.For<AvaloniaPropertyChangedEventArgs<int>>(e => ((LauncherProgressBar)e.Sender).OnPercentChanged(e.NewValue.Value)));
        StateProperty.Changed.Subscribe(Observer.For<AvaloniaPropertyChangedEventArgs<MainViewModel.ProgressState>>(e => ((LauncherProgressBar)e.Sender).OnStateChanged(e.NewValue.Value)));
    }

    private void OnPercentChanged(int percent)
    {
        _trackMiddle.Width = new GridLength(Math.Clamp(percent, 0, 100) * 2.53, GridUnitType.Pixel);
        _percentText.Text = $"{percent}%";
    }

    private void OnStateChanged(MainViewModel.ProgressState state)
    {
        Opacity = state == MainViewModel.ProgressState.Hidden ? 0 : 1;
        _borderEnable.IsVisible = state is MainViewModel.ProgressState.Empty or MainViewModel.ProgressState.Enabled;
        _borderFull.IsVisible = state == MainViewModel.ProgressState.Full;
        _disableGroup.IsVisible = state == MainViewModel.ProgressState.Disabled;
        _track.IsVisible = state == MainViewModel.ProgressState.Enabled;
        _percentGroup.IsVisible = state != MainViewModel.ProgressState.Disabled;
    }

    private static Image BorderImage(string asset) => new()
    {
        Source = Load(asset),
        Stretch = Stretch.None,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private static Image Segment(string asset, int column)
    {
        var image = new Image { Source = Load(asset), Stretch = Stretch.Fill };
        Grid.SetColumn(image, column);
        return image;
    }

    private static IImage Load(string asset) =>
        Skin.Load(asset) ?? throw new InvalidOperationException($"Missing asset {asset}");
}
