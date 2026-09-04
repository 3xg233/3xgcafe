using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace SpotlightLauncher.Clipboard;

public enum ClipboardKind { Text, Image }

/// <summary>单条剪贴板历史。</summary>
public sealed class ClipboardEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Time { get; set; } = DateTime.Now;
    public ClipboardKind Kind { get; set; }
    public string? Text { get; set; }
    /// <summary>图片：images 目录下的文件名（不含路径）。</summary>
    public string? ImageFile { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// 剪贴板历史：内存列表（新→旧）+ 磁盘持久化。
/// 文本嵌 JSON 存 history.json；图片存 images/{id}.png（缩略原图压缩可控）。
/// 搜索支持子串部分匹配 + 最长公共子串模糊覆盖 + 多词命中。
/// </summary>
public sealed class ClipboardStore
{
    public const int MaxTotalItems = 400;
    public const int MaxImageItems = 60;
    public const int MaxTextLength = 4000; // 单条文本裁剪上限，控制内存与检索成本

    private static string StoreDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotlightLauncher", "clipboard");
    private static string HistoryFile => Path.Combine(StoreDir, "history.json");
    private static string ImagesDir => Path.Combine(StoreDir, "images");

    private readonly object _lock = new();
    private readonly List<ClipboardEntry> _items = new(); // 保持新→旧

    public bool IsReady { get; private set; }
    public int Count { get { lock (_lock) return _items.Count; } }

    // ---------- 磁盘 ----------

    public void Load()
    {
        lock (_lock)
        {
            _items.Clear();
            try
            {
                if (File.Exists(HistoryFile))
                {
                    var arr = JsonSerializer.Deserialize<List<ClipboardEntry>>(File.ReadAllText(HistoryFile));
                    if (arr is not null)
                    {
                        // 剔除图片文件已丢失 / 超长文本的脏条目
                        foreach (var e in arr)
                        {
                            if (e.Kind == ClipboardKind.Image)
                            {
                                if (string.IsNullOrEmpty(e.ImageFile) ||
                                    !File.Exists(GetImageFullPath(e))) continue;
                            }
                            else if (string.IsNullOrEmpty(e.Text)) continue;
                            _items.Add(e);
                        }
                    }
                }
            }
            catch { /* 损坏则从空历史开始 */ }
            // 保证新→旧有序
            _items.Sort((a, b) => b.Time.CompareTo(a.Time));
            IsReady = true;
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(StoreDir);
                File.WriteAllText(HistoryFile, JsonSerializer.Serialize(_items));
            }
            catch { /* 写失败不影响使用 */ }
        }
    }

    public static string GetImageFullPath(ClipboardEntry e) =>
        Path.Combine(ImagesDir, e.ImageFile ?? "");

    // ---------- 新增 ----------

    /// <summary>返回 true 表示列表实际变化（需要落盘）。</summary>
    public bool AddText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length > MaxTextLength) text = text[..MaxTextLength];
        if (text.Length == 0) return false;

        lock (_lock)
        {
            // 去重：与最新文本相同 → 仅刷新时间置顶
            if (_items.Count > 0 && _items[0].Kind == ClipboardKind.Text
                && string.Equals(_items[0].Text, text, StringComparison.Ordinal))
            {
                _items[0].Time = DateTime.Now;
                return true;
            }
            // 与最近 N 条去重，避免高频重复复制刷屏
            int recent = Math.Min(_items.Count, 12);
            for (int i = 0; i < recent; i++)
            {
                if (_items[i].Kind == ClipboardKind.Text
                    && string.Equals(_items[i].Text, text, StringComparison.Ordinal))
                {
                    _items.RemoveAt(i);
                    break;
                }
            }

            _items.Insert(0, new ClipboardEntry { Kind = ClipboardKind.Text, Text = text });
            TrimLocked();
            return true;
        }
    }

    public bool AddImage(BitmapSource src)
    {
        if (src is null) return false;
        var id = Guid.NewGuid().ToString("N");
        string file = id + ".png";
        try
        {
            Directory.CreateDirectory(ImagesDir);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using (var fs = File.Create(Path.Combine(ImagesDir, file)))
                enc.Save(fs);
        }
        catch { return false; }

        lock (_lock)
        {
            _items.Insert(0, new ClipboardEntry
            {
                Kind = ClipboardKind.Image,
                ImageFile = file,
                Width = src.PixelWidth,
                Height = src.PixelHeight,
            });
            TrimLocked();
            return true;
        }
    }

    public void DeleteItem(ClipboardEntry e)
    {
        lock (_lock)
        {
            if (_items.Remove(e) && e.Kind == ClipboardKind.Image
                && !string.IsNullOrEmpty(e.ImageFile))
            {
                try { File.Delete(GetImageFullPath(e)); } catch { }
            }
        }
    }

    private void TrimLocked()
    {
        // 图片总数单独受限
        int imageCount = 0;
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].Kind == ClipboardKind.Image)
            {
                imageCount++;
                if (imageCount > MaxImageItems) DeleteAtLocked(i);
            }
        }
        while (_items.Count > MaxTotalItems) DeleteAtLocked(_items.Count - 1);
    }

    private void DeleteAtLocked(int index)
    {
        var e = _items[index];
        _items.RemoveAt(index);
        if (e.Kind == ClipboardKind.Image && !string.IsNullOrEmpty(e.ImageFile))
        {
            try { File.Delete(GetImageFullPath(e)); } catch { }
        }
    }

    // ---------- 读取 / 搜索 ----------

    public IReadOnlyList<ClipboardEntry> Snapshot() { lock (_lock) return _items.ToArray(); }

    /// <summary>空关键词 → 全部（新→旧）。有关键词 → 文本条目按相关度排序。</summary>
    public List<ClipboardEntry> Search(string? query, int max)
    {
        List<ClipboardEntry> all;
        lock (_lock) all = new List<ClipboardEntry>(_items);

        if (string.IsNullOrWhiteSpace(query)) return all.Take(max).ToList();

        string q = query.Trim();
        var scored = new List<(ClipboardEntry E, double S)>();
        foreach (var e in all)
        {
            if (e.Kind != ClipboardKind.Text || string.IsNullOrEmpty(e.Text)) continue;
            double s = TextRelevance(q, e.Text);
            if (s > 0) scored.Add((e, s));
        }
        return scored
            .OrderByDescending(x => x.S)
            .ThenByDescending(x => x.E.Time)
            .Take(max)
            .Select(x => x.E)
            .ToList();
    }

    /// <summary>
    /// 文本相关性：整串包含 > 长公共子串覆盖率 > 多词全部命中。
    /// 阈值以下返回 0（不展示），实现"部分文本内容 + 一定程度模糊识别"。
    /// </summary>
    private static double TextRelevance(string q, string text)
    {
        string lq = q.ToLowerInvariant();
        string lt = text.ToLowerInvariant();

        // 1) 整串子串包含（大小写不敏感）→ 最强
        if (lt.Contains(lq, StringComparison.Ordinal))
        {
            // 文本越短/命中越靠前略加分
            double pos = (double)lt.IndexOf(lq, StringComparison.Ordinal) / Math.Max(1, lt.Length);
            return 1.0 + (1.0 - pos) * 0.15;
        }

        // 2) 最长公共子串覆盖率：支持打断/部分片段
        if (lq.Length >= 4)
        {
            int lcs = LongestCommonSubstring(lq, lt);
            double cov = (double)lcs / lq.Length;
            if (cov >= 0.45) return 0.55 + cov * 0.35;
            if (cov >= 0.30) return cov * 0.5;
        }

        // 3) 空格/标点分词：所有词均命中（任一子串）视为相关
        var words = lq.Split(new[] { ' ', '\t', '，', ',', '。', '、', '；', ';', '：', ':' },
            StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words.All(w => w.Length >= 2 && lt.Contains(w, StringComparison.Ordinal)))
            return 0.45;

        return 0;
    }

    private static int LongestCommonSubstring(string a, string b)
    {
        // 滚动数组 DP；a 为短串（关键词），b 为长文本
        int n = a.Length, m = b.Length;
        if (n == 0 || m == 0) return 0;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        int best = 0;
        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : 0;
                if (cur[j] > best) best = cur[j];
            }
            (prev, cur) = (cur, prev);
            Array.Clear(cur);
        }
        return best;
    }
}
