using Avalonia;
using Avalonia.Media;

namespace Wcs.Desktop.Services;

public enum ThemeVariant
{
    Dark,
    Light
}

public class ThemeService
{
    private const string ConfigFile = "theme.txt";

    public ThemeVariant Current { get; private set; } = ThemeVariant.Dark;

    public ThemeService()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFile);
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                Current = text == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
            }
        }
        catch { }

        ApplyTheme(Current);
    }

    public void Toggle()
    {
        Current = Current == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        ApplyTheme(Current);

        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFile);
            File.WriteAllText(path, Current == ThemeVariant.Dark ? "Dark" : "Light");
        }
        catch { }
    }

    private static void ApplyTheme(ThemeVariant theme)
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        if (theme == ThemeVariant.Light)
        {
            resources["BgPrimary"] = Color.Parse("#F5F5F5");
            resources["BgSecondary"] = Color.Parse("#FFFFFF");
            resources["BgSidebar"] = Color.Parse("#FAFAFA");
            resources["BgStatusBar"] = Color.Parse("#E8E8E8");
            resources["BgCard"] = Color.Parse("#FFFFFF");
            resources["TabBg"] = Color.Parse("#F0F0F0");
            resources["TreeViewSelectedBg"] = Color.Parse("#E0E0E0");
            resources["BorderColor"] = Color.Parse("#E0E0E0");
            resources["TextPrimary"] = Color.Parse("#1E1E1E");
            resources["TextSecondary"] = Color.Parse("#666666");
        }
        else
        {
            resources["BgPrimary"] = Color.Parse("#1E1E1E");
            resources["BgSecondary"] = Color.Parse("#2A2A2A");
            resources["BgSidebar"] = Color.Parse("#252525");
            resources["BgStatusBar"] = Color.Parse("#1A1A1A");
            resources["TabBg"] = Color.Parse("#3A3A3A");
            resources["TreeViewSelectedBg"] = Color.Parse("#333333");
            resources["BgCard"] = Color.Parse("#333333");
            resources["BorderColor"] = Color.Parse("#404040");
            resources["TextPrimary"] = Color.Parse("#FFFFFF");
            resources["TextSecondary"] = Color.Parse("#9E9E9E");
        }
    }
}
