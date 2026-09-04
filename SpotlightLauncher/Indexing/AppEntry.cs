using System.IO;
using System.Windows.Media;

namespace SpotlightLauncher.Indexing;

/// <summary>一个可启动的应用程序条目。</summary>
public sealed class AppEntry
{
    public string Name { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string? IconPath { get; set; }      // 图标文件路径（可空）
    public int IconIndex { get; set; }
    public bool IsUwp { get; init; }           // 商店/UWP 应用（shell:AppsFolder 启动）

    /// <summary>启动次数（用于排序加权）。</summary>
    public int Frequency { get; set; }

    /// <summary>来源标签（用于区分索引渠道与界面展示）：快捷方式 / 程序文件 / Steam 游戏 / Epic 游戏。</summary>
    public string Source { get; init; } = "";

    /// <summary>可执行文件名（去扩展名），用于相似度匹配（Program Files 直扫项、快捷方式目标）。</summary>
    public string? ExeName { get; init; }

    /// <summary>所在目录名（TargetPath 的父文件夹），用于按"安装目录名"检索（如平铺安装 C:\Program Files\SomeApp\app.exe 时输入 SomeApp 可命中）。</summary>
    public string FolderName { get; set; } = "";

    /// <summary>拼音首字母串，如 "微信" -> "WX"。</summary>
    public string PinyinInitials { get; set; } = "";
    /// <summary>全拼（小写），如 "weixin"。</summary>
    public string PinyinFull { get; set; } = "";

    /// <summary>额外搜索关键词（如 UWP 包名 Microsoft.WindowsCalculator），不显示。</summary>
    public string SearchExtra { get; init; } = "";

    // ---------- UI 绑定辅助 ----------

    [System.Text.Json.Serialization.JsonIgnore]
    public ImageSource? Icon { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Subtitle =>
        IsUwp ? "Microsoft Store 应用"
              : (Source.Length > 0
                    ? Source
                    : (string.IsNullOrEmpty(Arguments) ? TargetPath : $"{TargetPath} {Arguments}"));
}
