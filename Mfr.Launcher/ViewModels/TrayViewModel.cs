using System;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;

namespace Mfr.Launcher.ViewModels;

/// <summary>Tray icon menu (Показать/Выход), mirrors FXTrayIcon of the old client.</summary>
public sealed class TrayViewModel
{
    private readonly Window _window;

    public TrayViewModel(Window window)
    {
        _window = window;
        ShowWindowCommand = new RelayCommand(Show);
        ExitCommand = new RelayCommand(Exit);
    }

    public RelayCommand ShowWindowCommand { get; }

    public RelayCommand ExitCommand { get; }

    public void Show()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Exit() => Environment.Exit(0);
}
