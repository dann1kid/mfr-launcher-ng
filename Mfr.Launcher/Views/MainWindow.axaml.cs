using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Mfr.Launcher.ViewModels;

namespace Mfr.Launcher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // debug helper: run with --opaque to paint the window background and
        // see exactly what the launcher itself renders
        if (Environment.GetCommandLineArgs().Contains("--opaque"))
        {
            TransparencyLevelHint = [];
            Background = Avalonia.Media.Brushes.DarkRed;
        }
        PointerPressed += OnPointerPressed;
        Opened += OnOpened;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        // manual dragging: only for empty areas, not for interactive controls
        if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            !IsOverInteractiveControl(args.Source as Avalonia.Visual))
        {
            BeginMoveDrag(args);
        }
    }

    private static bool IsOverInteractiveControl(Avalonia.Visual? source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Button or RepeatButton or HyperlinkButton
                or TextBox or CheckBox or RadioButton or ComboBox
                or ListBox or ScrollViewer or Slider or AutoCompleteBox)
            {
                return true;
            }
        }
        return false;
    }

    private void OnOpened(object? sender, EventArgs args)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.Window = this;
            _ = viewModel.InitializeAsync();
        }
    }

    private void OnMinimize(object? sender, RoutedEventArgs args) =>
        WindowState = WindowState.Minimized;

    private void OnExit(object? sender, RoutedEventArgs args)
    {
        // the old client minimized to tray instead of exiting when enabled
        if (DataContext is MainViewModel { MinimizeToTray: true })
        {
            Hide();
            return;
        }
        Environment.Exit(0);
    }
}
