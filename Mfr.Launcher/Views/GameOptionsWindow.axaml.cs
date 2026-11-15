using Avalonia.Controls;
using Mfr.Launcher.ViewModels;
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
        // тащим окно только за верхнюю шапку: прозрачные поля рамки — не ручка
        if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed && args.GetPosition(this).Y < 90)
        {
            BeginMoveDrag(args);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs args)
    {
        // задача окна не должна молотить в невидимке: закрытие снимает её
        if (DataContext is GameOptionsViewModel { IsBusy: true } viewModel)
        {
            viewModel.CancelRunningTask();
        }
        base.OnClosing(args);
    }

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => Close();
}
