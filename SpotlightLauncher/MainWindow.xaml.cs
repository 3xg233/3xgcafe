using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpotlightLauncher.Clipboard;
using SpotlightLauncher.Indexing;
using SpotlightLauncher.Native;

namespace SpotlightLauncher;

public partial class MainWindow : Window
{
    private readonly AppIndexer _indexer = new();
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private System.Windows.Threading.DispatcherTimer? _hideTimer;

    // ---------- 剪贴板历史 ----------
    private readonly ClipboardStore _clipStore = new();
    private bool _clipMode;      // false=软件查找，true=剪贴板历史
    private bool _clipReady;     // 历史已从磁盘加载
    private bool _altDown;       // Alt 防重复切换（按住不放不反复切）
    private System.Windows.Threading.DispatcherTimer? _clipDebounce;
    private ImageSource? _clipTextIcon;
    private readonly Dictionary<string, ImageSource> _thumbCache = new(StringComparer.OrdinalIgnoreCase);

    // 模式 Tab 配色（全限定，避开 System.Drawing.Brush 歧义）
    private static readonly System.Windows.Media.Brush BgActive = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x38));
    private static readonly System.Windows.Media.Brush BgInactive = Brushes.Transparent;
    private static readonly System.Windows.Media.Brush FgActive = new SolidColorBrush(Color.FromRgb(0xFA, 0xFC, 0x52));
    private static readonly System.Windows.Media.Brush FgInactive = new SolidColorBrush(Color.FromRgb(0x6A, 0x6C, 0x76));

    /// <summary>调试/自动化测试用：--no-autohide 时失活不自动隐藏。</summary>
    internal static bool NoAutoHide =
        Environment.GetCommandLineArgs().Contains("--no-autohide", StringComparer.OrdinalIgnoreCase);

    private static readonly string FreqFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpotlightLauncher", "freq.json");

    public MainWindow()
    {
        InitializeComponent();
        Height = 190; // 初始高度；结果增多后由代码自适应
        UpdateModeTabs(); // 初始 = 软件模式激活

        // 后台建索引；完成后回到 UI 线程加载使用频率并刷新一次列表
        // （频率字段与 UI 检索共享，必须在同一线程读写，避免数据竞争）
        _ = _indexer.BuildAsync().ContinueWith(_ =>
        {
            Dispatcher.Invoke(() =>
            {
                LoadFrequency();
                RefreshResults();
            });
        }, System.Threading.Tasks.TaskScheduler.Default);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 监听剪贴板（WM_CLIPBOARDUPDATE，事件驱动不占资源）
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProcClip);
        NativeMethods.AddClipboardFormatListener(hwnd);

        // 后台加载剪贴板历史，避免阻塞
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            _clipStore.Load();
            Dispatcher.BeginInvoke(() =>
            {
                _clipReady = true;
                if (_clipMode && IsVisible) RefreshResults();
            });
        });
    }

    // ---------- 显示 / 隐藏 ----------

    /// <summary>在鼠标所在屏幕居中偏上显示，并聚焦输入框。快捷键呼出默认软件查找模式。</summary>
    public void ShowLauncher()
    {
        if (!_indexer.IsReady)
            RefreshResults(); // 索引未就绪时先清空，就绪事件再刷

        // 每次呼出复位为软件查找模式
        if (_clipMode)
        {
            _clipMode = false;
            ReindexButton.Visibility = Visibility.Visible;
            UpdateModeTabs();
        }

        var wa = DisplayInfo.GetWorkAreaAtCursor();
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + wa.Height * 0.22;

        SearchBox.Clear();
        RefreshResults();
        Show();
        Activate();
        NativeMethods.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);

        // 下一消息循环再聚焦，确保窗口激活后输入框获得键盘焦点；未成功则延迟重试
        Dispatcher.BeginInvoke(() => FocusSearchBox(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void FocusSearchBox()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        if (!SearchBox.IsFocused)
        {
            // 窗口激活尚未完成时聚焦会失败，降到 ApplicationIdle 再试一次
            Dispatcher.BeginInvoke(() =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
                if (SearchBox.IsFocused) SearchBox.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        else
        {
            SearchBox.SelectAll();
        }
    }

    private void HideLauncher()
    {
        _hideTimer?.Stop();
        Hide();
    }

    private void OnWindowDeactivated(object sender, EventArgs e)
    {
        // 点击窗口外部时自动收起（Spotlight 行为）。
        // 加 500ms 防抖：短暂失活（如系统焦点抖动）不会误隐藏，恢复激活则取消。
        if (!IsVisible || NoAutoHide) return;
        _hideTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _hideTimer.Stop();
        _hideTimer.Tick -= OnHideTick;
        _hideTimer.Tick += OnHideTick;
        _hideTimer.Start();
    }

    private void OnHideTick(object? sender, EventArgs e)
    {
        _hideTimer?.Stop();
        if (IsVisible && !IsActive && !NoAutoHide)
            Hide();
    }

    // ---------- 模式切换（软件 / 剪贴板） ----------

    private void ToggleMode() => SetMode(!_clipMode);

    private void SetMode(bool clip)
    {
        if (_clipMode == clip) return;
        _clipMode = clip;
        ReindexButton.Visibility = clip ? Visibility.Collapsed : Visibility.Visible;
        UpdateModeTabs();
        RefreshResults();
    }

    private void OnModeTabClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string tag })
            SetMode(tag == "clip");
    }

    private void UpdateModeTabs()
    {
        bool clip = _clipMode;
        AppModeTab.Background = clip ? BgInactive : BgActive;
        AppModeText.Foreground = clip ? FgInactive : FgActive;
        ClipModeTab.Background = clip ? BgActive : BgInactive;
        ClipModeText.Foreground = clip ? FgActive : FgInactive;
        ModeSuffix.Text = clip ? " // CLIP_HISTORY" : " // APP_LAUNCHER";
    }

    // ---------- 键盘：Alt 切换模式 / 上下选择 / 回车执行 ----------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Alt 键切换软件 / 剪贴板模式（只响应单按，自动重复与组合键让行）
        if (e.Key == Key.System && (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt)
            && !e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control)
            && !e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (!_altDown)
            {
                _altDown = true;
                ToggleMode();
            }
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.System && (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt))
        {
            _altDown = false;
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyUp(e);
    }

    // ---------- 搜索 ----------

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => RefreshResults();

    // ---------- 重新索引（手动刷新：扫描新安装的软件）----------

    private void OnReindexClick(object sender, RoutedEventArgs e)
    {
        if (!_indexer.IsReady || !ReindexButton.IsEnabled) return;

        ReindexButton.IsEnabled = false;
        ReindexLabel.Text = "索引中…";
        ReindexButton.ToolTip = "正在扫描新安装的软件…";

        _ = _indexer.BuildAsync().ContinueWith(_ =>
        {
            Dispatcher.Invoke(() =>
            {
                LoadFrequency();
                RefreshResults();
                ReindexLabel.Text = "索引";
                ReindexButton.IsEnabled = true;
                ReindexButton.ToolTip = "重新索引：扫描新安装的软件";
            });
        }, System.Threading.Tasks.TaskScheduler.Default);
    }

    private void RefreshResults()
    {
        if (_clipMode) RefreshClipboardResults();
        else RefreshAppResults();
    }

    private void RefreshAppResults()
    {
        var results = AppIndexer.Search(_indexer.Entries, SearchBox.Text, 10);
        foreach (var entry in results)
            entry.Icon ??= LoadIcon(entry);

        ResultList.ItemsSource = results;
        ApplyListHeight(results.Count);
    }

    private void RefreshClipboardResults()
    {
        // 空关键词 → 全部内容（按时间倒序，由 store 维护）
        var list = _clipStore.Search(SearchBox.Text, int.MaxValue);
        var rows = new List<ClipboardRow>(list.Count);
        foreach (var it in list)
            rows.Add(new ClipboardRow { Icon = LoadClipIcon(it), Name = ClipName(it), Subtitle = ClipSubtitle(it), Entry = it });

        ResultList.ItemsSource = rows;
        if (rows.Count > 0) ResultList.SelectedIndex = 0;
        ApplyListHeight(rows.Count);
    }

    /// <summary>按结果数量自适应窗口高度（上限 ~11 行，其余滚动）。</summary>
    private void ApplyListHeight(int count)
    {
        Height = count > 0 ? Math.Clamp(102 + count * 40, 234, 524) : 234;
    }

    // ---------- 剪贴板捕获（WM_CLIPBOARDUPDATE → 防抖 → 读取存储） ----------

    private IntPtr WndProcClip(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            handled = true;
            _clipDebounce?.Stop();
            _clipDebounce ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _clipDebounce.Tick -= OnClipDebounceTick;
            _clipDebounce.Tick += OnClipDebounceTick;
            _clipDebounce.Start();
        }
        return IntPtr.Zero;
    }

    private void OnClipDebounceTick(object? sender, EventArgs e)
    {
        _clipDebounce?.Stop();
        CaptureClipboard();
    }

    private void CaptureClipboard()
    {
        if (!_clipReady) return;
        bool changed = false;
        try
        {
            if (System.Windows.Clipboard.ContainsImage())
            {
                var img = System.Windows.Clipboard.GetImage();
                if (img is not null) changed = _clipStore.AddImage(img);
            }
            else if (System.Windows.Clipboard.ContainsText())
            {
                string? t = System.Windows.Clipboard.GetText(TextDataFormat.UnicodeText);
                if (!string.IsNullOrWhiteSpace(t)) changed = _clipStore.AddText(t);
            }
        }
        catch (System.Runtime.InteropServices.ExternalException) { /* 剪贴板被占用，等待下次事件 */ }
        catch { /* 读取失败忽略 */ }

        if (changed)
        {
            _clipStore.Save();
            // 剪贴板模式且窗口可见时实时刷新
            if (_clipMode && IsVisible) RefreshResults();
        }
    }

    // ---------- 剪贴板条目显示辅助 ----------

    private ImageSource? LoadClipIcon(ClipboardEntry it)
    {
        if (it.Kind == ClipboardKind.Image)
        {
            string p = ClipboardStore.GetImageFullPath(it);
            if (File.Exists(p))
            {
                if (_thumbCache.TryGetValue(p, out var cached)) return cached;
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(p);
                    bi.DecodePixelWidth = 40; // 缩略解码，控制内存
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();
                    _thumbCache[p] = bi;
                    return bi;
                }
                catch { /* 解码失败回落文本图标 */ }
            }
        }
        return ClipTextIcon;
    }

    private ImageSource ClipTextIcon => _clipTextIcon ??= BuildClipTextIcon();

    /// <summary>程序化剪贴板图标（深底 + 荧光黄），避免依赖资源文件。</summary>
    private static ImageSource BuildClipTextIcon()
    {
        using var bmp = new System.Drawing.Bitmap(16, 16);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.FromArgb(0x14, 0x18, 0x1F));
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0xFF, 0xFA, 0xFC, 0x52), 1.3f);
            g.DrawLine(pen, 6, 2, 10, 2);       // 顶部夹条
            g.DrawLine(pen, 6, 2, 6, 5);
            g.DrawLine(pen, 10, 2, 10, 5);
            g.DrawRectangle(pen, 3, 4, 10, 10); // 板身
            using var line = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0xA0, 0xFA, 0xFC, 0x52), 1.1f);
            g.DrawLine(line, 5, 9, 11, 9);
            g.DrawLine(line, 5, 12, 11, 12);
        }
        return bmp.ToBitmapSource();
    }

    private static string ClipName(ClipboardEntry it)
    {
        if (it.Kind == ClipboardKind.Image)
            return it.Width > 0 && it.Height > 0 ? $"图片  {it.Width}×{it.Height}" : "图片";
        string t = it.Text ?? "";
        foreach (var raw in t.Split('\n'))
        {
            string s = raw.Trim('\r', ' ', '\t');
            if (s.Length > 0)
                return s.Length > 60 ? s[..60] + "…" : s;
        }
        return t.Length > 60 ? t[..60] + "…" : t;
    }

    private static string ClipSubtitle(ClipboardEntry it) =>
        it.Kind == ClipboardKind.Image
            ? $"{it.Time:MM-dd HH:mm} · 图片"
            : $"{it.Time:MM-dd HH:mm} · {(it.Text?.Length ?? 0)} 字符";

    private void CopyBack(ClipboardRow row)
    {
        try
        {
            var it = row.Entry;
            if (it.Kind == ClipboardKind.Text && it.Text is not null)
            {
                System.Windows.Clipboard.SetText(it.Text, TextDataFormat.UnicodeText);
            }
            else if (it.Kind == ClipboardKind.Image)
            {
                string p = ClipboardStore.GetImageFullPath(it);
                if (File.Exists(p))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(p);
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    System.Windows.Clipboard.SetImage(bi);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制失败：{ex.Message}", "SpotlightLauncher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        HideLauncher();
    }

    // ---------- 键盘 / 鼠标执行 ----------

    private void OnSearchBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 右键菜单对应的快捷键：Ctrl+Shift+F/C/Enter（仅软件模式指向文件时有效）
        if (!_clipMode && e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control) &&
            e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (e.Key == Key.F && ResultList.SelectedItem is AppEntry entryF)
            {
                OpenFileLocation(entryF);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.C && ResultList.SelectedItem is AppEntry entryC)
            {
                OnCopyPathClick(sender, e);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && ResultList.SelectedItem is AppEntry entryA)
            {
                RunAsAdmin(entryA);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Down && ResultList.Items.Count > 0)
        {
            ResultList.SelectedIndex = Math.Min(ResultList.SelectedIndex + 1, ResultList.Items.Count - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && ResultList.Items.Count > 0)
        {
            ResultList.SelectedIndex = Math.Max(ResultList.SelectedIndex - 1, 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            LaunchSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideLauncher();
            e.Handled = true;
        }
    }

    private void OnResultListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            LaunchSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideLauncher();
            e.Handled = true;
        }
    }

    private void OnResultListClick(object sender, MouseButtonEventArgs e)
    {
        if (_clipMode)
        {
            if (ResultList.SelectedItem is ClipboardRow row)
                CopyBack(row);
        }
        else if (ResultList.SelectedItem is AppEntry entry)
        {
            Launch(entry);
        }
    }

    private void LaunchSelected()
    {
        if (_clipMode)
        {
            if (ResultList.SelectedItem is ClipboardRow row)
                CopyBack(row);
        }
        else if (ResultList.SelectedItem is AppEntry entry)
        {
            Launch(entry);
        }
    }

    // ---------- 右键：选中所指项 + 打开文件所在位置（仅软件模式有意义） ----------

    private void OnResultListPreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        // 右键时先选中指针下方的列表项，确保右键菜单作用于正确的条目
        var hit = System.Windows.Media.VisualTreeHelper.HitTest(ResultList, e.GetPosition(ResultList));
        var v = hit?.VisualHit;
        while (v is not null && v is not ListBoxItem)
            v = System.Windows.Media.VisualTreeHelper.GetParent(v);
        if (v is ListBoxItem item)
            ResultList.SelectedItem = item.DataContext;
    }

    private void OnResultListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // 三项菜单均仅当软件模式指向真实文件（非 Store 应用）时启用
        bool usable = !_clipMode
            && ResultList.SelectedItem is AppEntry entry
            && !entry.IsUwp
            && !string.IsNullOrEmpty(entry.TargetPath)
            && File.Exists(entry.TargetPath);
        OpenLocationMenuItem.IsEnabled = usable;
        CopyPathMenuItem.IsEnabled = usable;
        RunAsAdminMenuItem.IsEnabled = usable;
    }

    private void OnOpenFileLocationClick(object sender, RoutedEventArgs e)
    {
        if (ResultList.SelectedItem is AppEntry entry)
            OpenFileLocation(entry);
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (ResultList.SelectedItem is AppEntry entry
            && !string.IsNullOrEmpty(entry.TargetPath)
            && File.Exists(entry.TargetPath))
        {
            try { System.Windows.Clipboard.SetText(entry.TargetPath); }
            catch (Exception ex)
            {
                MessageBox.Show($"复制路径失败：{ex.Message}", "SpotlightLauncher",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void OnRunAsAdminClick(object sender, RoutedEventArgs e)
    {
        if (ResultList.SelectedItem is AppEntry entry)
            RunAsAdmin(entry);
    }

    private void RunAsAdmin(AppEntry entry)
    {
        try
        {
            if (entry.IsUwp)
            {
                MessageBox.Show("Microsoft Store 应用不支持以管理员身份运行。",
                    "SpotlightLauncher", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrEmpty(entry.TargetPath) || !File.Exists(entry.TargetPath))
            {
                MessageBox.Show("找不到目标文件，无法启动。",
                    "SpotlightLauncher", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var psi = new ProcessStartInfo
            {
                FileName = entry.TargetPath,
                UseShellExecute = true,
                Verb = "runas", // 触发 UAC 提权
            };
            if (!string.IsNullOrEmpty(entry.Arguments)) psi.Arguments = entry.Arguments;
            if (!string.IsNullOrEmpty(entry.WorkingDirectory) && Directory.Exists(entry.WorkingDirectory))
                psi.WorkingDirectory = entry.WorkingDirectory;
            Process.Start(psi);

            entry.Frequency++;
            SaveFrequencyAsync();
            HideLauncher();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 弹窗点了“否”，静默忽略
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败：{ex.Message}", "SpotlightLauncher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFileLocation(AppEntry entry)
    {
        try
        {
            if (entry.IsUwp)
            {
                MessageBox.Show("Microsoft Store 应用没有可直接打开的文件位置。",
                    "SpotlightLauncher", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrEmpty(entry.TargetPath) || !File.Exists(entry.TargetPath))
            {
                MessageBox.Show("找不到目标文件，无法打开所在位置。",
                    "SpotlightLauncher", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string folder = Path.GetDirectoryName(entry.TargetPath)!;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开位置失败：{ex.Message}", "SpotlightLauncher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- 启动 ----------

    private void Launch(AppEntry entry)
    {
        try
        {
            if (entry.IsUwp)
            {
                // UWP：通过 ShellExecute 激活 shell:AppsFolder\...
                NativeMethods.ShellExecuteW(IntPtr.Zero, null, entry.TargetPath, null, null, 5);
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = entry.TargetPath,
                    UseShellExecute = true,
                };
                if (!string.IsNullOrEmpty(entry.Arguments)) psi.Arguments = entry.Arguments;
                if (!string.IsNullOrEmpty(entry.WorkingDirectory) && Directory.Exists(entry.WorkingDirectory))
                    psi.WorkingDirectory = entry.WorkingDirectory;
                Process.Start(psi);
            }

            // 记录使用频率（异步落盘）
            entry.Frequency++;
            SaveFrequencyAsync();

            HideLauncher();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败：{ex.Message}", "SpotlightLauncher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- 应用图标 ----------

    private ImageSource? LoadIcon(AppEntry entry)
    {
        try
        {
            string? path = !string.IsNullOrEmpty(entry.IconPath) && File.Exists(entry.IconPath)
                ? entry.IconPath
                : (!entry.IsUwp && File.Exists(entry.TargetPath) ? entry.TargetPath : null);
            if (path is null) return null;

            if (_iconCache.TryGetValue(path, out var cached)) return cached;

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;

            using var bmp = icon.ToBitmap();
            var source = bmp.ToBitmapSource();
            _iconCache[path] = source;
            return source;
        }
        catch { return null; }
    }

    // ---------- 使用频率 ----------

    private void LoadFrequency()
    {
        try
        {
            if (!File.Exists(FreqFile)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FreqFile));
            if (map is null) return;
            foreach (var e in _indexer.Entries)
                if (map.TryGetValue(e.TargetPath, out var freq)) e.Frequency = freq;
        }
        catch { /* 频率文件损坏忽略 */ }
    }

    private async void SaveFrequencyAsync()
    {
        try
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _indexer.Entries)
                if (e.Frequency > 0) map[e.TargetPath] = e.Frequency;

            var dir = Path.GetDirectoryName(FreqFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(FreqFile, JsonSerializer.Serialize(map));
        }
        catch { /* 写失败不影响使用 */ }
    }
}

/// <summary>结果列表通用行：应用条目与剪贴板条目共用模板字段（Icon/Name/Subtitle）。</summary>
public sealed class ClipboardRow
{
    public ImageSource? Icon { get; init; }
    public string Name { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public ClipboardEntry Entry { get; init; } = null!;
}

// ---------- Bitmap -> BitmapSource 扩展 ----------

public static class BitmapExtensions
{
    public static BitmapSource ToBitmapSource(this System.Drawing.Bitmap bmp)
    {
        IntPtr hbmp = bmp.GetHbitmap();
        try
        {
            return Imaging.CreateBitmapSourceFromHBitmap(
                hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            NativeMethods.DeleteObject(hbmp);
        }
    }
}
