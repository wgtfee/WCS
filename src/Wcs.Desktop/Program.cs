using AtomUI;
using Avalonia;

namespace Wcs.Desktop;

public static class Program
{
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithAtomUIDefaultOptions()
            .LogToTrace();
    }
}
