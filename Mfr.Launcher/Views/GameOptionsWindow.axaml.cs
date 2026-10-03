using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace Mfr.Launcher.Views;

public partial class GameOptionsWindow : Window
{
    public GameOptionsWindow()
    {
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

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => Close();
}
