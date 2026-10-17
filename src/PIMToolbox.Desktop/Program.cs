using Avalonia;

namespace PIMToolbox.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: "PIMToolbox.SingleInstance",
            createdNew: out var createdNew);
        if (!createdNew)
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
