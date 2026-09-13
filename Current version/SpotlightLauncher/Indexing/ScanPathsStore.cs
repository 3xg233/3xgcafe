using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SpotlightLauncher.Indexing;

/// <summary>
/// 自定义扫描路径（scanpaths.json）的存储与增删。
/// UI 线程持有实例并增删（每次变更立即落盘）；索引器重建时用 LoadPathsFromFile() 从磁盘读取最新状态。
/// 读兼容旧格式（字符串数组 / { path, name, keywords } 对象），写统一为字符串数组（name/keywords 有意丢弃）。
/// </summary>
public sealed class ScanPathsStore
{
    private static string StoreDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "3xgcafe", "Spotlight");

    public static string CustomPathsFile => Path.Combine(StoreDir, "scanpaths.json");

    private readonly object _lock = new();
    private readonly List<string> _paths = new();

    /// <summary>当前路径列表快照（新→旧与添加顺序一致，即添加顺序）。</summary>
    public IReadOnlyList<string> Paths
    {
        get { lock (_lock) return _paths.ToArray(); }
    }

    /// <summary>从磁盘加载到内存（文件不存在或损坏时为空列表）。</summary>
    public void Load()
    {
        lock (_lock)
        {
            _paths.Clear();
            foreach (var p in LoadPathsFromFile())
                _paths.Add(p);
        }
    }

    /// <summary>校验并添加一条路径；重复或非法返回 false。成功后立即落盘。</summary>
    public bool Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        path = path.Trim();
        if (!IsValidPath(path)) return false;
        lock (_lock)
        {
            if (_paths.Contains(path, StringComparer.OrdinalIgnoreCase)) return false;
            _paths.Add(path);
        }
        Save();
        return true;
    }

    /// <summary>移除一条路径（忽略大小写）；不存在返回 false。成功后立即落盘。</summary>
    public bool Remove(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        path = path.Trim();
        bool removed;
        lock (_lock)
        {
            int i = _paths.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            removed = i >= 0;
            if (removed) _paths.RemoveAt(i);
        }
        if (removed) Save();
        return removed;
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(StoreDir);
                File.WriteAllText(CustomPathsFile, JsonSerializer.Serialize(_paths));
            }
            catch { /* 写失败不影响使用 */ }
        }
    }

    /// <summary>路径合法：存在的 .exe 文件，或存在的目录。</summary>
    public static bool IsValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return File.Exists(path);
        return Directory.Exists(path);
    }

    /// <summary>从磁盘解析路径列表（兼容旧对象格式，仅取 path 字段），供索引器后台线程使用。</summary>
    public static List<string> LoadPathsFromFile()
    {
        var result = new List<string>();
        try
        {
            if (!File.Exists(CustomPathsFile)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(CustomPathsFile));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string? p = null;
                if (el.ValueKind == JsonValueKind.String) p = el.GetString();
                else if (el.ValueKind == JsonValueKind.Object)
                    p = GetJsonStr(el, "path") ?? GetJsonStr(el, "Path");
                if (!string.IsNullOrWhiteSpace(p))
                    result.Add(p!.Trim('"').Trim());
            }
        }
        catch { /* 损坏则返回空列表 */ }
        return result;
    }

    private static string? GetJsonStr(JsonElement r, string prop)
    {
        if (r.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }
}
