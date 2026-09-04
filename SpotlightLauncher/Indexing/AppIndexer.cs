using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SpotlightLauncher.Indexing;

/// <summary>
/// 应用索引：扫描开始菜单/桌面/快速启动的快捷方式、桌面直接放置的 .exe、%LOCALAPPDATA%\Programs、
/// Program Files 下所有可执行文件、Steam / Epic 等第三方下载器条目，以及用户在 scanpaths.json 中自定义的路径；
/// 按与输入内容的相似度排序。
/// </summary>
public sealed class AppIndexer
{
    private readonly object _sync = new();
    private List<AppEntry> _entries = new();

    /// <summary>用户自定义额外扫描根目录（JSON 字符串数组）。文件不存在时忽略，不影响其它来源。</summary>
    private static readonly string CustomPathsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpotlightLauncher", "scanpaths.json");

    public IReadOnlyList<AppEntry> Entries
    {
        get { lock (_sync) return _entries; }
    }

    public bool IsReady { get; private set; }

    /// <summary>后台线程构建索引（首次较慢，一次性）。</summary>
    public Task BuildAsync()
    {
        return Task.Run(Build);
    }

    private void Build()
    {
        var list = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ScanShortcuts(list, seen, seenTargets);   // 所有 .lnk/.url（开始菜单/桌面/快速启动）
        ScanDesktopExes(list, seen, seenTargets); // 桌面直接放置的 .exe（便携程序/绿色软件）
        ScanLocalProgramsExes(list, seen, seenTargets); // %LOCALAPPDATA%\Programs 下的 .exe（Discord/Slack 等）
        ScanUwp(list, seen, seenTargets);          // Microsoft Store / UWP
        ScanSteam(list, seen, seenTargets);        // Steam 库（steam:// 拉起）
        ScanEpic(list, seen, seenTargets);         // Epic 清单
        ScanProgramFilesExes(list, seen, seenTargets); // 所有 Program Files 下的 .exe
        ScanCustomPaths(list, seen, seenTargets);  // 用户自定义目录（scanpaths.json）

        ComputePinyin(list);
        foreach (var en in list)
        {
            if (!en.IsUwp && !string.IsNullOrEmpty(en.TargetPath))
            {
                try { en.FolderName = Path.GetFileName(Path.GetDirectoryName(en.TargetPath)) ?? ""; }
                catch { /* 路径异常则留空 */ }
            }
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        lock (_sync) _entries = list;
        IsReady = true;
    }

    // ---------- 扫描所有快捷方式（.lnk / .url）----------

    /// <summary>去重键：目标路径 + 参数。同一 exe 配不同参数（如游戏启动器的 --game=xxx）应视为不同条目。</summary>
    private static string DedupKey(string target, string args) => target + "\u0001" + (args ?? "");

    private static void ScanShortcuts(List<AppEntry> entries, HashSet<string> seen, HashSet<string> seenTargets)
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Microsoft\Windows\Start Menu\Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch"),
        };

        object? shell = null;
        try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); }
        catch { shell = null; }

        foreach (var dir in roots)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

            // --- .lnk 快捷方式（应用 / 游戏 / UWP）---
            if (shell is not null)
            {
                foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                {
                    try
                    {
                        dynamic shortcut = shell.GetType().InvokeMember(
                            "CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                            new object[] { lnk })!;

                        string target = (string)shortcut.TargetPath;
                        string args = (string)shortcut.Arguments ?? "";
                        if (string.IsNullOrWhiteSpace(target)) continue;

                        string name = Path.GetFileNameWithoutExtension(lnk);
                        // 去重键含参数：同一 exe 配不同 --game=xxx 视为不同条目（如多个游戏共用一个启动器）
                        if (!seenTargets.Add(DedupKey(target, args))) continue;
                        if (!seen.Add(name)) continue;

                        string iconLocation = (string)shortcut.IconLocation ?? "";
                        bool isUwp = target.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase)
                                     || lnk.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0;

                        entries.Add(new AppEntry
                        {
                            Name = name,
                            TargetPath = target,
                            Arguments = args,
                            WorkingDirectory = (string)shortcut.WorkingDirectory ?? "",
                            IconPath = ParseIconPath(iconLocation, out int idx),
                            IconIndex = idx,
                            IsUwp = isUwp,
                            Source = "快捷方式",
                            ExeName = isUwp ? null : Path.GetFileNameWithoutExtension(target),
                        });
                    }
                    catch { /* 单个快捷方式损坏不影响整体 */ }
                }
            }

            // --- .url 快捷方式（Internet 快捷 / 协议启动器）---
            // 跳过纯 http(s) 书签；steam:// 交给 ScanSteam 统一处理；保留其它协议与本地文件路径。
            foreach (var url in Directory.EnumerateFiles(dir, "*.url", SearchOption.AllDirectories))
            {
                try
                {
                    string? link = ReadUrlTarget(url);
                    if (string.IsNullOrWhiteSpace(link)) continue;
                    if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                    if (link.StartsWith("steam://", StringComparison.OrdinalIgnoreCase)) continue;

                    string name = Path.GetFileNameWithoutExtension(url);
                    if (!seenTargets.Add(DedupKey(link, ""))) continue;
                    if (!seen.Add(name)) continue;

                    entries.Add(new AppEntry
                    {
                        Name = name,
                        TargetPath = link,
                        Arguments = "",
                        WorkingDirectory = "",
                        IsUwp = false,
                        Source = "快捷方式",
                        ExeName = null,
                    });
                }
                catch { /* 单个 .url 损坏不影响整体 */ }
            }
        }
    }

    /// <summary>读取 .url 文件中的目标（支持 file:// 本地路径与协议 URL）。</summary>
    private static string? ReadUrlTarget(string urlPath)
    {
        try
        {
            foreach (var line in File.ReadAllLines(urlPath))
            {
                if (!line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) continue;
                var v = line.Substring(4).Trim();
                if (v.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    try { return new Uri(v).LocalPath; } catch { return v; }
                }
                return v;
            }
        }
        catch { }
        return null;
    }

    // ---------- 扫描 UWP / 商店应用----------

    private static void ScanUwp(List<AppEntry> entries, HashSet<string> seen, HashSet<string> seenTargets)
    {
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            foreach (var pkg in pm.FindPackagesForUser(""))
            {
                try
                {
                    foreach (var app in pkg.GetAppListEntries())
                    {
                        try
                        {
                            string aumid = app.AppUserModelId;
                            if (string.IsNullOrEmpty(aumid)) continue;

                            string name = app.DisplayInfo.DisplayName;
                            if (string.IsNullOrWhiteSpace(name)) name = pkg.DisplayName;
                            if (string.IsNullOrWhiteSpace(name)) continue;

                            string target = "shell:AppsFolder\\" + aumid;
                            if (!seen.Add(name)) continue;
                            if (!seenTargets.Add(DedupKey(target, ""))) continue;

                            entries.Add(new AppEntry
                            {
                                Name = name,
                                TargetPath = target,
                                IsUwp = true,
                                Source = "Microsoft Store 应用",
                                SearchExtra = pkg.Id.Name,
                            });
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { /* 无商店应用或权限受限时忽略 */ }
    }

    // ---------- 扫描 Steam 游戏库（steam://rungameid 拉起）----------

    private static void ScanSteam(List<AppEntry> entries, HashSet<string> seen, HashSet<string> seenTargets)
    {
        string? root = FindSteamRoot();
        if (root is null) return;

        var libs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        string lf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(lf)) ParseLibraryFolders(lf, libs);

        foreach (var lib in libs)
        {
            var sa = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(sa)) continue;

            foreach (var acf in Directory.EnumerateFiles(sa, "appmanifest_*.acf"))
            {
                try
                {
                    string text = File.ReadAllText(acf);
                    string? id = MatchValue(text, "appid");
                    string? name = MatchValue(text, "name");
                    if (id is null || name is null) continue;

                    string key = "steam:" + id;
                    if (!seenTargets.Add(DedupKey(key, ""))) continue;
                    if (!seen.Add(name)) continue;

                    string iconPath = FindSteamGameExe(sa, name) ?? "";
                    entries.Add(new AppEntry
                    {
                        Name = name,
                        TargetPath = $"steam://rungameid/{id}",
                        IsUwp = false,
                        Source = "Steam 游戏",
                        IconPath = iconPath.Length > 0 ? iconPath : null,
                    });
                }
                catch { }
            }
        }
    }

    private static string? FindSteamRoot()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var p = k?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
        }
        catch { }

        foreach (var c in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam"),
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
        })
            if (Directory.Exists(c)) return c;

        return null;
    }

    private static void ParseLibraryFolders(string file, HashSet<string> libs)
    {
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                var m = Regex.Match(line, "\"(?:path)\"\\s+\"([A-Za-z]:[^\"]*)\"");
                if (m.Success && Directory.Exists(m.Groups[1].Value)) libs.Add(m.Groups[1].Value);
                var m2 = Regex.Match(line, "\"\\d+\"\\s+\"([A-Za-z]:\\\\[^\"]*)\"");
                if (m2.Success && Directory.Exists(m2.Groups[1].Value)) libs.Add(m2.Groups[1].Value);
            }
        }
        catch { }
    }

    private static string? FindSteamGameExe(string steamapps, string name)
    {
        try
        {
            var common = Path.Combine(steamapps, "common");
            if (!Directory.Exists(common)) return null;
            foreach (var d in Directory.EnumerateDirectories(common))
            {
                if (!string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase)) continue;
                string? best = null; long bestSize = -1;
                foreach (var e in Directory.EnumerateFiles(d, "*.exe", SearchOption.AllDirectories))
                {
                    try
                    {
                        long s = new FileInfo(e).Length;
                        if (s > bestSize) { bestSize = s; best = e; }
                    }
                    catch { }
                }
                if (best is not null) return best;
            }
        }
        catch { }
        return null;
    }

    // ---------- 扫描 Epic 游戏清单（.item JSON）----------

    private static void ScanEpic(List<AppEntry> entries, HashSet<string> seen, HashSet<string> seenTargets)
    {
        var dir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (!Directory.Exists(dir)) return;

        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(item));
                var r = doc.RootElement;
                var name = GetJsonStr(r, "DisplayName");
                var install = GetJsonStr(r, "InstallLocation");
                var exe = GetJsonStr(r, "LaunchExecutable");
                if (name is null || install is null || exe is null) continue;

                string full = Path.IsPathRooted(exe) ? exe : Path.Combine(install, exe);
                if (!File.Exists(full)) continue;

                if (!seenTargets.Add(DedupKey(full, ""))) continue;
                if (!seen.Add(name)) continue;

                entries.Add(new AppEntry
                {
                    Name = name,
                    TargetPath = full,
                    IsUwp = false,
                    Source = "Epic 游戏",
                    ExeName = Path.GetFileNameWithoutExtension(full),
                });
            }
            catch { }
        }
    }

    private static string? GetJsonStr(System.Text.Json.JsonElement r, string prop)
    {
        if (r.TryGetProperty(prop, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
            return v.GetString();
        return null;
    }

    // ---------- 扫描 Program Files 下所有 .exe（过滤系统/卸载器噪声）----------

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Common Files", "WindowsApps", "ModifiableWindowsApps", "Windows Defender", "Windows Mail",
        "Windows Media Player", "Windows Photo Viewer", "Windows Sidebar", "WindowsPowerShell",
        "Windows NT", "Internet Explorer", "Reference Assemblies", "PackageManagement", "dotnet",
        "Microsoft.NET", "Microsoft SDKs", "Windows Kits", "MSBuild", "InstallShield Installation Information",
        "Uninstall Information", "$Recycle.Bin", "SystemApps", "Windows Security", "Windows Portable Devices",
        "Windows Feedback Hub", "Windows Capabilities", "ContainerManager", "WINDOWS~1", "Microsoft Visual Studio",
        "NVIDIA Corporation", "Intel", "AMD", "Lenovo", "Qualcomm", "Realtek", "Broadcom",
        "QQPCMgr", // 电脑管家后台助手进程群；其主程序由开始菜单快捷方式覆盖
    };

    private const int MaxEntries = 2500;

    private static void ScanProgramFilesExes(List<AppEntry> entries, HashSet<string> seen, HashSet<string> seenTargets)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
            WalkExes(root, entries, seen, seenTargets, "程序文件");
    }

    /// <summary>扫描桌面（用户/公共）直接放置的 .exe 文件（便携程序、绿色软件），含子文件夹。</summary>
    private static void ScanDesktopExes(List<AppEntry> entries, HashSet<string> seen, HashSet<string> seenTargets)
    {
        foreach (var desk in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        })
        {
            if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk)) continue;
            WalkExes(desk, entries, seen, seenTargets, "桌面程序");
        }
    }

    /// <summary>扫描 %LOCALAPPDATA%\Programs 下直接安装的 .exe（Discord、Slack、各类 Electron 应用等），含子文件夹。</summary>
    private static void ScanLocalProgramsExes(List<AppEntry> entries, HashSet<string> seen, HashSet<string> seenTargets)
    {
        string? local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return;
        string root = Path.Combine(local, "Programs");
        if (!Directory.Exists(root)) return;
        WalkExes(root, entries, seen, seenTargets, "用户程序");
    }

    /// <summary>
    /// 扫描用户在 scanpaths.json（%APPDATA%\SpotlightLauncher\scanpaths.json）中列出的自定义路径。
    /// 支持两种写法：
    ///   1) 字符串：目录则递归扫其下 .exe；以 .exe 结尾则直接收录该单个可执行文件。
    ///   2) 对象：{ "path": "目录或exe", "name": "友好名称(可选)", "keywords": "额外搜索词(可选)" }，
    ///      便于把 exe 名与用户熟悉的名字（如中文名）对应起来，使按名字搜索也能命中。
    /// 用于收录各种非标准安装位置的软件，无需改动代码即可扩展。文件不存在或格式错误时静默跳过。
    /// </summary>
    private static void ScanCustomPaths(List<AppEntry> entries, HashSet<string> seen, HashSet<string> seenTargets)
    {
        try
        {
            if (!File.Exists(CustomPathsFile)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(CustomPathsFile));
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string? path = null;
                string? dispName = null;
                string? keywords = null;

                if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    path = el.GetString();
                }
                else if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    path = GetJsonStr(el, "path") ?? GetJsonStr(el, "Path");
                    dispName = GetJsonStr(el, "name") ?? GetJsonStr(el, "Name");
                    keywords = GetJsonStr(el, "keywords") ?? GetJsonStr(el, "Keywords");
                }

                if (string.IsNullOrWhiteSpace(path)) continue;
                path = path!.Trim('"').Trim();

                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    string name = string.IsNullOrWhiteSpace(dispName) ? Path.GetFileNameWithoutExtension(path) : dispName!;
                    if (!seen.Add(name)) continue;
                    if (!seenTargets.Add(path)) continue;
                    entries.Add(new AppEntry
                    {
                        Name = name,
                        TargetPath = path,
                        IsUwp = false,
                        Source = "自定义路径",
                        ExeName = Path.GetFileNameWithoutExtension(path),
                        SearchExtra = keywords ?? "",
                    });
                }
                else if (Directory.Exists(path))
                {
                    WalkExes(path, entries, seen, seenTargets, "自定义路径");
                }
            }
        }
        catch { /* 自定义配置文件损坏则忽略，不影响其它来源 */ }
    }

    private static void WalkExes(string dir, List<AppEntry> entries, HashSet<string> seen, HashSet<string> seenTargets, string source = "程序文件")
    {
        if (entries.Count >= MaxEntries) return;

        try
        {
            foreach (var sd in Directory.EnumerateDirectories(dir))
            {
                if (ExcludedDirs.Contains(Path.GetFileName(sd))) continue;
                WalkExes(sd, entries, seen, seenTargets, source);
                if (entries.Count >= MaxEntries) return;
            }
        }
        catch { /* 无权限目录跳过 */ }

        try
        {
            foreach (var exe in Directory.EnumerateFiles(dir, "*.exe"))
            {
                if (entries.Count >= MaxEntries) return;
                if (IsNoiseExe(Path.GetFileName(exe))) continue;

                string name = Path.GetFileNameWithoutExtension(exe);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!seen.Add(name)) continue;
                if (!seenTargets.Add(exe)) continue;

                entries.Add(new AppEntry
                {
                    Name = name,
                    TargetPath = exe,
                    IsUwp = false,
                    Source = source,
                    ExeName = name,
                });
            }
        }
        catch { }
    }

    /// <summary>过滤明显不可直接启动的后台助手 / 安装 / 卸载程序（其主程序通常由开始菜单快捷方式覆盖）。</summary>
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "unins", "uninst", "uninstall", "vcredist", "redist", "install", "setup", "_install", "stub",
        "rundll32", "regsvr32", "msiexec", "crash", "crashreport", "diag", "report", "7z", "aria2",
        "helper", "service", "svc", "agent", "proxy", "tray", "inject", "hook", "controller",
        "monitor", "daemon", "diagnos", "repair", "bootstrapper", "runtime", "plugin", "bridge",
        "launcherentrance", "remote", "webtool", "recommend", "ocr", "statecheck", "snapshot",
        "update", "updater", "host",
    };

    private static bool IsNoiseExe(string fn)
    {
        string n = Path.GetFileNameWithoutExtension(fn).ToLowerInvariant();
        if (n.Length == 0) return true;
        foreach (var t in NoiseTokens)
            if (n.Contains(t)) return true;
        if (n.EndsWith("ext")) return true; // 浏览器扩展类助手，如 WeixinExt / QMChromeExt
        return false;
    }

    /// <summary>解析快捷方式 IconLocation（格式 "路径[,索引]"），返回路径并输出索引。</summary>
    private static string? ParseIconPath(string iconLocation, out int index)
    {
        index = 0;
        var s = (iconLocation ?? "").Trim().Trim('"');
        if (s.Length == 0) return null;
        int comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..], out index))
            return s[..comma].Trim().Trim('"');
        return s;
    }

    private static string? MatchValue(string text, string key)
    {
        var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    // ---------- 拼音 ----------

    private static void ComputePinyin(IEnumerable<AppEntry> entries)
    {
        foreach (var e in entries)
        {
            try
            {
                e.PinyinInitials = PinyinHelper.ToInitials(e.Name);
                e.PinyinFull = PinyinHelper.ToFull(e.Name);
            }
            catch { /* 拼音失败不影响使用 */ }
        }
    }

    // ---------- 相似度搜索 ----------

    /// <summary>按 query 与条目的相似度过滤并排序，返回前 limit 条。</summary>
    public static List<AppEntry> Search(IReadOnlyList<AppEntry> entries, string query, int limit = 10)
    {
        if (entries.Count == 0) return new List<AppEntry>();

        if (string.IsNullOrWhiteSpace(query))
        {
            return entries
                .OrderByDescending(e => e.Frequency)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        string q = query.Trim().ToLowerInvariant();
        var scored = new List<(AppEntry Entry, int Score)>();

        foreach (var e in entries)
        {
            double best = 0;
            best = Math.Max(best, Similarity(e.Name, q));
            if (e.PinyinInitials.Length > 0) best = Math.Max(best, Similarity(e.PinyinInitials, q));
            if (e.PinyinFull.Length > 0) best = Math.Max(best, Similarity(e.PinyinFull, q));
            if (e.ExeName is not null) best = Math.Max(best, Similarity(e.ExeName, q));
            if (e.FolderName.Length > 0) best = Math.Max(best, Similarity(e.FolderName, q));
            if (e.SearchExtra.Length > 0) best = Math.Max(best, Similarity(e.SearchExtra, q));

            if (best <= 0) continue;

            int score = (int)(best * 1000);
            if (e.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) score += 60;
            if (e.ExeName is not null && e.ExeName.StartsWith(query, StringComparison.OrdinalIgnoreCase)) score += 30;
            score += e.Frequency * 10;

            scored.Add((e, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Entry)
            .ToList();
    }

    /// <summary>归一化相似度 [0,1]：完全相等=1；包含关系≈0.85~1；否则用双字母 bigram 的 Dice 系数。</summary>
    private static double Similarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (a == b) return 1;
        if (a.Contains(b) || b.Contains(a))
        {
            int longer = Math.Max(a.Length, b.Length);
            int shorter = Math.Min(a.Length, b.Length);
            return 0.85 + 0.15 * (shorter / (double)longer);
        }
        return Dice(a, b);
    }

    private static double Dice(string a, string b)
    {
        var ba = Bigrams(a);
        var bb = Bigrams(b);
        if (ba.Count == 0 || bb.Count == 0) return 0;
        int inter = 0;
        foreach (var g in ba)
            if (bb.Contains(g)) inter++;
        return (2.0 * inter) / (ba.Count + bb.Count);
    }

    private static HashSet<string> Bigrams(string s)
    {
        var set = new HashSet<string>();
        if (s.Length <= 1) { if (s.Length == 1) set.Add(s); return set; }
        for (int i = 0; i < s.Length - 1; i++)
            set.Add(s.Substring(i, 2));
        return set;
    }
}
