using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Mfr.Launcher.ViewModels;
using Mfr.Launcher.Views;

namespace Mfr.Launcher;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.MainWindow = mainWindow;

            // tray menu (Показать/Выход), mirrors FXTrayIcon of the old client
            var tray = new TrayViewModel(mainWindow);
            DataContext = tray;
            var menu = new NativeMenu
            {
                new NativeMenuItem { Header = "Показать", Command = tray.ShowWindowCommand },
                new NativeMenuItemSeparator(),
                new NativeMenuItem { Header = "Выход", Command = tray.ExitCommand },
            };
            using var iconStream = Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://Mfr.Launcher/Assets/icon.png"), baseUri: null);
            SetValue(TrayIcon.IconsProperty, new TrayIcons
            {
                new TrayIcon
                {
                    Icon = new WindowIcon(iconStream),
                    ToolTipText = "M[FR] Launcher",
                    Menu = menu,
                    IsVisible = true,
                },
            });

            mainWindow.Show();

            if (Environment.GetCommandLineArgs().Contains("--show-options"))
            {
                viewModel.ConfigureGameCommand.Execute(null);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
