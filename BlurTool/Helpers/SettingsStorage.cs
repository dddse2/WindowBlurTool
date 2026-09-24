using System.Text.Json;
using BlurTool.Models;

namespace BlurTool.Helpers;

internal static class SettingsStorage
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlurTool");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

                // v5：捕获方式枚举改为 Magpie 命名（GraphicsCapture/DesktopDuplication/GDI/DwmSharedSurface）。
                if (settings.SettingsVersion < 5)
                {
                    // 旧值：0 = 屏幕像素(BitBlt)，1 = 窗口内容(PrintWindow)，2 = Windows Graphics Capture。
                    settings.CaptureMode = settings.CaptureMode switch
                    {
                        2 => (int)WindowCaptureMode.GraphicsCapture,
                        _ => (int)WindowCaptureMode.GDI
                    };
                    settings.SettingsVersion = 5;
                }

                return settings;
            }
        }
        catch
        {
            // ignore corrupted settings
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // ignore write failures
        }
    }
}
