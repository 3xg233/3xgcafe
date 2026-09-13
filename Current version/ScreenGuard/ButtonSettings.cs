using System;
using System.IO;
using System.Text.Json;

namespace ScreenGuard;

/// <summary>
/// 桌面按钮位置持久化：%APPDATA%\ScreenGuard\button.json（WPF 逻辑坐标）。
/// 独立于 settings.json，避免与主设置互相干扰。
/// </summary>
public static class ButtonSettings
{
    public sealed record Pos(double Left, double Top);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SettingsPath
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root))
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                root = Path.Combine(profile, "AppData", "Roaming");
            }
            return Path.Combine(root, "3xgcafe", "Guard", "button.json");
        }
    }

    public static Pos? Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Pos>(File.ReadAllText(SettingsPath), JsonOptions);
        }
        catch
        {
            // 配置损坏时回退默认位置
        }
        return null;
    }

    public static void Save(Pos pos)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(pos, JsonOptions));
        }
        catch
        {
            // 保存失败不影响使用（下次回到默认位置）
        }
    }
}
