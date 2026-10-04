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
            // v2 stack (dev 3.3.x) composed up front; MainViewModel activates it only
            // when the server actually answers /v2 — against v1 production the probe
            // fails fast and everything stays on the v1 path
            var services = new Mfr.Core.Tasks.LauncherServices();
            var httpV2 = new System.Net.Http.HttpClient();
            var apiV2 = new Mfr.Core.Network.ApiClientV2(
                httpV2, services.Options.Server.RuLocationAddress, services.Options.Server.EuLocationAddress);
            apiV2.SetClientId(services.Options.ClientId);
            var downloaderV2 = new Mfr.Core.Network.HttpFileDownloader(httpV2);
            var v2 = new Mfr.Core.V2.LauncherServicesV2(services, apiV2, downloaderV2);

            var viewModel = new MainViewModel(services, v2);
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
            if (GetValue(TrayIcon.IconsProperty) is TrayIcons { Count: > 0 } trayIcons)
            {
                trayIcons[0].Clicked += (_, _) => tray.Show();
            }

            mainWindow.Show();

            if (Environment.GetCommandLineArgs().Contains("--show-options"))
            {
                viewModel.ConfigureGameCommand.Execute(null);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
