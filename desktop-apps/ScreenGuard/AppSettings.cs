using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenGuard;

/// <summary>
/// 应用设置：%APPDATA%\ScreenGuard\settings.json
/// 密码只存 PBKDF2 加盐哈希，绝不明文落盘。
/// </summary>
public sealed class AppSettings
{
    /// <summary>空闲多少分钟后锁定（1–180）。</summary>
    public int IdleMinutes { get; set; } = 5;

    public string? PasswordHash { get; set; }

    public string? PasswordSalt { get; set; }

    /// <summary>锁屏界面上的自定义提示语（可为空）。</summary>
    public string Hint { get; set; } = "";

    /// <summary>锁屏期间阻止屏幕关闭（默认关闭，避免干扰系统电源策略）。</summary>
    public bool KeepDisplayOn { get; set; }

    /// <summary>锁屏背景实时模糊（DWM 亚克力，可透出后方程序大致运行状态）；关闭则用纯深色背景。亚克力不可用时自动回退模糊快照。</summary>
    public bool BlurBackground { get; set; } = true;

    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash) && !string.IsNullOrEmpty(PasswordSalt);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string SettingsDir
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root))
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                root = Path.Combine(profile, "AppData", "Roaming");
            }
            return Path.Combine(root, "3xgcafe", "Guard");
        }
    }

    public static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    loaded.IdleMinutes = Math.Clamp(loaded.IdleMinutes, 1, 180);
                    loaded.Hint ??= "";
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置损坏时回退默认值，不影响程序启动
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
