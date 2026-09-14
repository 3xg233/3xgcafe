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
    // ---------- 自定义扫描路径 ----------
    private readonly ScanPathsStore _scanPaths = new(); // UI 线程增删、立即落盘;索引器从磁盘读
    private bool _pendingRebuild; // 索引未就绪时收到重建请求,首轮构建完成后补一次
    private System.Windows.Threading.DispatcherTimer? _statusTimer;
    private bool _clipMode;      // false=软件查找，true=剪贴板历史
    private bool _clipReady;     // 历史已从磁盘加载
    private bool _altDown;       // Alt 防重复切换（按住不放不反复切）
    private System.Windows.Threading.DispatcherTimer? _clipDebounce;
    private bool _pathsDialogOpen; // 对话框打开期间挂起自动隐藏
    /// <summary>"失活时保持窗口"偏好（App 在启动与托盘切换时同步写入）。取消勾选瞬间若窗口已失活,补一次自动隐藏判定。</summary>
    private bool _keepOnDeactivate;
    // ---------- 供 3xgcafe Console 状态通道读取 ----------

    /// <summary>应用索引是否已构建完成。</summary>
    public bool IndexReady => _indexer.IsReady;

    /// <summary>已索引的应用条目数。</summary>
    public int IndexedCount => _indexer.Entries.Count;

    /// <summary>剪贴板历史是否已加载完成。</summary>
    public bool ClipboardReady => _clipReady;

    /// <summary>剪贴板历史条数。</summary>
    public int ClipboardCount => _clipStore.Search(null, ClipboardStore.MaxTotalItems).Count;

    public bool KeepOnDeactivate
    {
        get => _keepOnDeactivate;
        set
        {
            _keepOnDeactivate = value;
            if (!value) EvaluateAutoHide();
        }
    }
    private ImageSource? _clipTextIcon;
    private readonly Dictionary<string, ImageSource> _thumbCache = new(StringComparer.OrdinalIgnoreCase);

    // 模式 Tab 配色（全限定，避开 System.Drawing.Brush 歧义）
    private static readonly System.Windows.Media.Brush BgActive = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x38));
    private static readonly System.Windows.Media.Brush BgInactive = Brushes.Transparent;
    private static readonly System.Windows.Media.Brush FgActive = new SolidColorBrush(Color.FromRgb(0xFA, 0xFC, 0x52));
    private static readonly System.Windows.Media.Brush FgInactive = new SolidColorBrush(Color.FromRgb(0x6A, 0x6C, 0x76));

    /// <summary>调试/自动化测试用：--no-autohide 时失活不自动隐藏。</summary>
    private static bool NoAutoHide =
        Environment.GetCommandLineArgs().Contains("--no-autohide", StringComparer.OrdinalIgnoreCase);

    private static readonly string FreqFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "3xgcafe", "Spotlight", "freq.json");

    private static readonly string WindowPosFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "3xgcafe", "Spotlight", "window.json");

    /// <summary>标题行按住拖动窗口;结束后保存位置。</summary>
    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); }
        catch (InvalidOperationException) { /* 鼠标状态异常时忽略 */ }
        SaveWindowPos();
    }

    private void SaveWindowPos()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return; // 窗口从未显示过时不写 NaN
        try
        {
            var dir = Path.GetDirectoryName(WindowPosFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(WindowPosFile, JsonSerializer.Serialize(new { Left, Top }));
        }
        catch { /* 写失败不影响使用 */ }
    }

    /// <summary>读档并校验窗口上边缘中点在任一屏幕内;有效则应用位置,无效返回 false 走默认定位。</summary>
    private bool TryRestoreWindowPos()
    {
        try
        {
            if (!File.Exists(WindowPosFile)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(WindowPosFile));
            if (doc.RootElement.TryGetProperty("Left", out var l)
                && doc.RootElement.TryGetProperty("Top", out var t)
                && l.ValueKind == System.Text.Json.JsonValueKind.Number
                && t.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                double left = l.GetDouble(), top = t.GetDouble();
                if (double.IsFinite(left) && double.IsFinite(top)
                    && DisplayInfo.IsPointOnAnyScreen(left + Width / 2, top + 24))
                {
                    Left = left;
                    Top = top;
                    return true;
                }
            }
        }
        catch { /* 损坏则回退默认位置 */ }
        return false;
    }

    public MainWindow()
    {
        InitializeComponent();
        Height = 190; // 初始高度；结果增多后由代码自适应
        _scanPaths.Load();
        UpdateModeTabs(); // 初始 = 软件模式激活

        // 后台建索引；完成后回到 UI 线程加载使用频率并刷新一次列表
        // （频率字段与 UI 检索共享，必须在同一线程读写，避免数据竞争）
        _ = _indexer.BuildAsync().ContinueWith(_ =>
        {
            Dispatcher.Invoke(() =>
            {
                LoadFrequency();
                RefreshResults();
                // 索引就绪前若收到过重建请求(如启动瞬间拖放添加),此刻补一次
                if (_pendingRebuild)
                {
                    _pendingRebuild = false;
                    RebuildIndex();
                }
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
            AddPathButton.Visibility = Visibility.Visible;
            UpdateModeTabs();
        }

        if (!TryRestoreWindowPos())
        {
            var wa = DisplayInfo.GetWorkAreaAtCursor();
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + wa.Height * 0.22;
        }

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

    public void HideLauncher()
    {
        _hideTimer?.Stop();
        SaveWindowPos();
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveWindowPos();
        base.OnClosed(e);
    }

    private void OnWindowDeactivated(object sender, EventArgs e)
    {
        // 点击窗口外部时自动收起（Spotlight 行为）。
        // 加 500ms 防抖：短暂失活（如系统焦点抖动）不会误隐藏，恢复激活则取消。
        EvaluateAutoHide();
    }

    /// <summary>判定并启动自动隐藏定时器（可见、无豁免、且当前未激活时）。</summary>
    private void EvaluateAutoHide()
    {
        if (!IsVisible || NoAutoHide || KeepOnDeactivate) return;
        if (_pathsDialogOpen) return; // 路径管理对话框打开期间不自动隐藏
        if (IsActive) return; // 已激活则无需隐藏

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
        // 鼠标左键按住（拖拽进行中）时不隐藏：重新起表延后判断，松开后再恢复原逻辑
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0)
        {
            _hideTimer?.Start();
            return;
        }
        if (IsVisible && !IsActive && !NoAutoHide && !KeepOnDeactivate)
            Hide();
    }

    // ---------- 模式切换（软件 / 剪贴板） ----------

    private void ToggleMode() => SetMode(!_clipMode);

    private void SetMode(bool clip)
    {
        if (_clipMode == clip) return;
        _clipMode = clip;
        ReindexButton.Visibility = clip ? Visibility.Collapsed : Visibility.Visible;
        AddPathButton.Visibility = clip ? Visibility.Collapsed : Visibility.Visible;
        UpdateModeTabs();
        RefreshResults();
    }

    private void OnModeTabClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // 阻止冒泡到标题行触发窗口拖动
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

    // ---------- 扫描路径管理对话框 ----------

    private void OnAddPathClick(object sender, RoutedEventArgs e) => OpenSettingsDialog();

    /// <summary>
    /// 打开扫描路径管理对话框（本工具唯一的设置界面）。
    /// 同时供 3xgcafe Console 的「设置」按钮经 IPC（cmd=settings）调用。
    /// </summary>
    public void OpenSettingsDialog()
    {
        // 对话框以主窗口为 Owner，主窗口隐藏时先呼出，避免定位异常
        if (!IsVisible)
            ShowLauncher();

        var dlg = new PathsDialog(_scanPaths) { Owner = this };
        _pathsDialogOpen = true;
        try
        {
            dlg.ShowDialog();
        }
        finally
        {
            _pathsDialogOpen = false;
        }
        if (dlg.Changed) RebuildIndex();
        Activate();
        FocusSearchBox();
    }

    // ---------- 重新索引（手动刷新：扫描新安装的软件）----------

    private void OnReindexClick(object sender, RoutedEventArgs e)
    {
        if (!_indexer.IsReady || !ReindexButton.IsEnabled) return;
        RebuildIndex();
    }

    /// <summary>重建索引（索引按钮 / 对话框关闭 / 拖放添加三处共用）。</summary>
    private void RebuildIndex()
    {
        if (!_indexer.IsReady)
        {
            _pendingRebuild = true; // 首轮索引构建中:挂起,构建完成后补一次
            return;
        }

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

    /// <summary>底部状态条:显示 msg,2.5 秒后自动隐藏。</summary>
    private void ShowStatus(string msg)
    {
        StatusText.Text = msg;
        StatusBar.Visibility = Visibility.Visible;
        _statusTimer?.Stop();
        _statusTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(2500),
        };
        _statusTimer.Tick -= OnStatusTick;
        _statusTimer.Tick += OnStatusTick;
        _statusTimer.Start();
    }

    private void OnStatusTick(object? sender, EventArgs e)
    {
        _statusTimer?.Stop();
        StatusBar.Visibility = Visibility.Collapsed;
    }

    // ---------- 拖放添加扫描路径 ----------

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (_clipMode) return; // 剪贴板模式不接收文件拖放（与"+"按钮隐藏一致）
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return; // 文本等拖放留给默认行为（搜索框）
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] files
                    && files.Any(ScanPathsStore.IsValidPath)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (_clipMode || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        int added = 0, dup = 0, invalid = 0;
        foreach (var f in files)
        {
            if (!ScanPathsStore.IsValidPath(f)) { invalid++; continue; }
            if (_scanPaths.Add(f)) added++;
            else dup++;
        }
        e.Handled = true;

        if (added > 0)
        {
            RebuildIndex();
            ShowStatus(dup > 0
                ? $"已添加 {added} 个路径,忽略 {dup} 个重复 · 正在重建索引"
                : $"已添加 {added} 个路径 · 正在重建索引");
        }
        else if (dup > 0) ShowStatus($"已忽略 {dup} 个重复路径");
        else if (invalid > 0) ShowStatus("拖入的内容不是有效的 .exe 或文件夹");

        // 拖放完成后带回前台:否则窗口仍失活,下一 tick 会立即隐藏,用户错过状态条反馈
        Activate();
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
            MessageBox.Show($"复制失败：{ex.Message}", "3xgcafe Spotlight",
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
                MessageBox.Show($"复制路径失败：{ex.Message}", "3xgcafe Spotlight",
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
                    "3xgcafe Spotlight", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrEmpty(entry.TargetPath) || !File.Exists(entry.TargetPath))
            {
                MessageBox.Show("找不到目标文件，无法启动。",
                    "3xgcafe Spotlight", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show($"启动失败：{ex.Message}", "3xgcafe Spotlight",
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
                    "3xgcafe Spotlight", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrEmpty(entry.TargetPath) || !File.Exists(entry.TargetPath))
            {
                MessageBox.Show("找不到目标文件，无法打开所在位置。",
                    "3xgcafe Spotlight", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string folder = Path.GetDirectoryName(entry.TargetPath)!;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开位置失败：{ex.Message}", "3xgcafe Spotlight",
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
            MessageBox.Show($"启动失败：{ex.Message}", "3xgcafe Spotlight",
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
