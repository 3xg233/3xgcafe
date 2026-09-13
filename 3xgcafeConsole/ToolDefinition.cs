using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThreeXGCafeConsole;

/// <summary>卡片上的快捷操作（走工具的命名管道通道）。</summary>
public sealed class ToolAction
{
    public string Label { get; set; } = "";
    public string Cmd { get; set; } = "";
    public Dictionary<string, string>? Args { get; set; }
}

/// <summary>
/// 一个被管理工具的定义。**新增工具只需往 tools.json 里加一条**，
/// 面板无需改动代码即可显示卡片、启停、状态与快捷操作。
/// </summary>
public sealed class ToolDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string ExePath { get; set; } = "";

    /// <summary>命名管道名（工具侧 = "3xgcafe-" + 工具Id）；留空表示该工具暂无状态通道。</summary>
    public string PipeName { get; set; } = "";

    /// <summary>开机自启的注册表值名（HKCU\...\Run），默认取 Id。</summary>
    public string RunValueName { get; set; } = "";

    /// <summary>内置工具（不可删除）。</summary>
    public bool BuiltIn { get; set; }

    public List<ToolAction> QuickActions { get; set; } = new();

    [JsonIgnore]
    public string EffectiveRunValueName => string.IsNullOrWhiteSpace(RunValueName) ? Id : RunValueName;

    [JsonIgnore]
    public string ProcessName => string.IsNullOrEmpty(ExePath)
        ? Id
        : Path.GetFileNameWithoutExtension(ExePath);
}

public sealed class ToolConfig
{
    public List<ToolDefinition> Tools { get; set; } = new();
}

/// <summary>tools.json 的读写（%APPDATA%\3xgcafe\Console\tools.json）。首次运行自动生成默认配置。</summary>
public static class ToolConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ConfigDir
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root))
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                root = Path.Combine(profile, "AppData", "Roaming");
            }
            return Path.Combine(root, "3xgcafe", "Console");
        }
    }

    public static string ConfigPath => Path.Combine(ConfigDir, "tools.json");

    /// <summary>绿色版存放目录：桌面\3xgcafe\Current version\便携版\（可按需改 tools.json 里的 exePath）。</summary>
    public static string DefaultToolDir
    {
        get
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop))
                desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
            return Path.Combine(desktop, "3xgcafe", "Current version", "便携版");
        }
    }

    public static ToolConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<ToolConfig>(File.ReadAllText(ConfigPath), JsonOptions);
                if (cfg is { Tools.Count: > 0 })
                {
                    foreach (ToolDefinition t in cfg.Tools)
                        Normalize(t);
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏时重建默认配置
        }

        var fresh = CreateDefault();
        Save(fresh);
        return fresh;
    }

    public static void Save(ToolConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch
        {
            // 保存失败不阻断使用
        }
    }

    private static void Normalize(ToolDefinition t)
    {
        t.Name ??= t.Id;
        t.Subtitle ??= "";
        t.ExePath ??= "";
        t.RunValueName ??= "";
        t.PipeName ??= "";
        t.QuickActions ??= new List<ToolAction>();
    }

    private static ToolDefinition Built(string id, string name, string subtitle, params ToolAction[] actions)
        => new()
        {
            Id = id,
            Name = name,
            Subtitle = subtitle,
            ExePath = Path.Combine(DefaultToolDir, "3xgcafe-" + id + ".exe"),
            PipeName = "3xgcafe-" + id,
            RunValueName = "3xgcafe-" + id,
            BuiltIn = true,
            QuickActions = new List<ToolAction>(actions)
        };

    private static ToolConfig CreateDefault() => new()
    {
        Tools =
        {
            Built("Monitor", "3xgcafe Monitor", "桌面性能监控", new ToolAction { Label = "唤起", Cmd = "show" },
                new ToolAction { Label = "置顶", Cmd = "toggle_topmost" }),
            Built("Spotlight", "3xgcafe Spotlight", "启动器 / 剪贴板", new ToolAction { Label = "打开", Cmd = "show" }),
            Built("Wallpaper", "3xgcafe Wallpaper", "桌面图标显隐", new ToolAction { Label = "切换图标", Cmd = "toggle_icons" }),
            Built("Guard", "3xgcafe Guard", "空闲自动锁定",
                new ToolAction { Label = "立即锁定", Cmd = "lock" },
                new ToolAction { Label = "暂停 30 分", Cmd = "pause", Args = new Dictionary<string, string> { ["minutes"] = "30" } },
                new ToolAction { Label = "恢复监控", Cmd = "resume" })
        }
    };
}
