using System.Reflection;
using Avalonia;

namespace Wcs.Desktop.Services;

/// <summary>
/// 通过反射访问 Avalonia 内部 API 获取 AtomUI 的 IThemeManager
/// </summary>
public static class ThemeManagerAccessor
{
    public static T? GetService<T>() where T : class
    {
        // AvaloniaLocator get_Current 在 Ava12 中被移除
        // AtomUI 内部通过 get_CurrentMutable() + BindToSelf 注册 ThemeManager
        // 我们通过反射找所有可能的方式来获取
        try
        {
            var locatorType = typeof(AvaloniaLocator);

            // 尝试 get_CurrentMutable（Ava11 内部 API）
            var method = locatorType.GetMethod("get_CurrentMutable",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (method is null)
            {
                // 尝试 get_Current
                method = locatorType.GetMethod("get_Current",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            }

            var locator = method?.Invoke(null, null);
            if (locator is null) return null;

            var getService = locator.GetType().GetMethod("GetService", [typeof(Type)]);
            return getService?.Invoke(locator, [typeof(T)]) as T;
        }
        catch
        {
            return null;
        }
    }
}
