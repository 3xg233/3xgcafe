using System.Text;
using Microsoft.International.Converters.PinYinConverter;

namespace SpotlightLauncher.Indexing;

/// <summary>
/// 中文拼音工具：缓存每个汉字的拼音，支持全拼与首字母两种输出。
/// </summary>
public static class PinyinHelper
{
    // 汉字 -> 全拼（小写，无声调）
    private static readonly Dictionary<char, string> FullCache = new();
    // 汉字 -> 首字母（大写）
    private static readonly Dictionary<char, char> InitialCache = new();
    private static readonly object Sync = new();

    /// <summary>取字符串的拼音首字母串（大写），如 "微信" -> "WX"。非中文字符：字母原样大写，数字保留。</summary>
    public static string ToInitials(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (IsCjk(c)) sb.Append(GetInitial(c));
            else if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>取字符串的全拼（小写，无声调），如 "微信" -> "weixin"。非中文字符原样小写。</summary>
    public static string ToFull(string text)
    {
        var sb = new StringBuilder(text.Length * 3);
        foreach (var c in text)
        {
            if (IsCjk(c)) sb.Append(GetFull(c));
            else if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF;

    private static char GetInitial(char c)
    {
        lock (Sync)
        {
            if (InitialCache.TryGetValue(c, out var init)) return init;
            var full = LookupFull(c);
            char result = full.Length > 0 ? char.ToUpperInvariant(full[0]) : '\0';
            InitialCache[c] = result;
            return result;
        }
    }

    private static string GetFull(char c)
    {
        lock (Sync)
        {
            if (FullCache.TryGetValue(c, out var full)) return full;
            var result = LookupFull(c);
            FullCache[c] = result;
            return result;
        }
    }

    private static string LookupFull(char c)
    {
        try
        {
            var cc = new ChineseChar(c);
            foreach (var p in cc.Pinyins)
            {
                if (string.IsNullOrEmpty(p)) continue;
                // 拼音带声调数字后缀，如 "WEI1"；多音字取第一个
                return p.TrimEnd('1', '2', '3', '4', '5').ToLowerInvariant();
            }
        }
        catch { /* 个别生僻字可能抛异常 */ }
        return "";
    }
}
