using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Input;

namespace Mfr.Launcher.Views;

public partial class DonationWindow : Window
{
    public DonationWindow()
    {
        DataContext = new ViewModel(Close);
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

    private sealed class ViewModel(Action close)
    {
        public RelayCommand CloseCommand { get; } = new(close);
    }
}
