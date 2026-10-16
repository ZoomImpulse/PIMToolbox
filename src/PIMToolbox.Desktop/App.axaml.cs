using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;

namespace PIMToolbox.Desktop;

public partial class App : Application
{
    public bool IsExiting { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        var trayIcons = TrayIcon.GetIcons(this);
        if (trayIcons is { Count: > 0 })
            trayIcons[0].Icon = TrayIconFactory.Create();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { Icon = TrayIconFactory.Create() };
        base.OnFrameworkInitializationCompleted();
    }

    private void OnTrayShow(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { } window)
        {
            window.Show();
            window.Activate();
        }
    }

    private void OnTrayExit(object? sender, EventArgs e)
    {
        IsExiting = true;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
