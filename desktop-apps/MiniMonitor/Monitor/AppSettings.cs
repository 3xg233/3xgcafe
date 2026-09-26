using System;
using System.IO;
using System.Text.Json;

namespace MiniMonitor.Monitor;

public class AppSettings
{
    public double? X { get; set; }
    public double? Y { get; set; }
    public bool Topmost { get; set; } = false;
    public bool AutoStart { get; set; }
    public bool ClickThrough { get; set; }
    public double Opacity { get; set; } = 1.0;
    /// <summary>两行磁盘指标监控的盘符（1~2 个字母，如 ["C","E"]）；不配置或无效时自动检测前两个固定盘。</summary>
    public string[]? Drives { get; set; }

    /// <summary>要显示的指标 id 列表（如 ["cpu","mem"]）；为 null 表示全部显示，向后兼容。</summary>
    public string[]? VisibleMetrics { get; set; }

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "3xgcafe", "Monitor", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* 配置损坏时使用默认值 */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 写失败不影响运行 */ }
    }
}
