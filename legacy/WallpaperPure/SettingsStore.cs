using System;
using System.IO;
using System.Text.Json;

namespace WallpaperPure;

/// <summary>
/// 位置持久化：%APPDATA%\WallpaperPure\settings.json
/// </summary>
internal static class SettingsStore
{
    public sealed record WindowPos(double Left, double Top);

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WallpaperPure");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static WindowPos? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<WindowPos>(File.ReadAllText(FilePath));
        }
        catch
        {
            return null;
        }
    }

    public static void Save(WindowPos pos)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(pos));
        }
        catch
        {
            // 持久化失败（如磁盘只读）不影响当次功能
        }
    }
}
