using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Shapes;
using System.Windows.Interop;
using Microsoft.Win32;
using MiniMonitor.Monitor;
using MiniMonitor.Native;

namespace MiniMonitor;

public partial class MainWindow : Window
{
    private readonly SysMonitor _monitor;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _savePositionTimer;
    private readonly AppSettings _settings;

    private sealed record MetricDef(string Id, string Name, Grid Row);
    private readonly List<MetricDef> _metrics = new();
    private readonly HashSet<string> _visibleMetrics = new(StringComparer.OrdinalIgnoreCase);

    public event Action? OnRequestExit;

    public bool IsTopmost { get; private set; }
    public bool IsAutoStart { get; private set; }
    public bool IsWindowVisible { get; private set; } = true;
    public bool IsClickThrough { get; private set; }

    private HwndSource? _hwndSource;
    private bool _hotkeyRegistered;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _monitor = new SysMonitor(_settings.Drives);
        ApplyHardwareLabels();
        BuildMetricRegistry();
        InitVisibleMetrics();
        Root.ContextMenu = BuildContextMenu();
        ApplyMetricVisibility();

        IsTopmost = _settings.Topmost;
        IsAutoStart = RegistryAutoStartEnabled();
        IsClickThrough = _settings.ClickThrough;
        Topmost = IsTopmost;
        Opacity = Math.Clamp(_settings.Opacity, 0.4, 1.0);

        ApplyStartupPosition();
        RefreshMenuChecks();

        // 显示后由系统权威校验窗口是否真的落在某块屏上（IsPointOnAnyScreen 只是预检，
        // 多屏 + 不同 DPI 缩放时可能误判；不在任何屏上则自动复位）
        Loaded += (_, _) => EnsureOnScreen();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();

        // 拖动后延时保存位置，避免频繁写盘；窗口关闭前也保存一次
        _savePositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _savePositionTimer.Tick += (_, _) =>
        {
            _savePositionTimer.Stop();
            SaveSettings();
        };
        LocationChanged += (_, _) =>
        {
            _savePositionTimer.Stop();
            _savePositionTimer.Start();
        };
        Closing += (_, _) => SaveSettings();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hotkeyRegistered && _hwndSource != null)
            WindowNative.UnregisterHotKey(_hwndSource.Handle, WindowNative.HotkeyId);
        _savePositionTimer?.Stop();
        _monitor.Dispose();
        base.OnClosed(e);
    }

    // ---------- 界面刷新 ----------

    private void RefreshUi()
    {
        SetBar(CpuBar, CpuTrack, CpuText, _monitor.CpuPercent, p => $"{p:F0}%");
        // 温度按 0~100°C 映射
        SetBar(TempBar, TempTrack, TempText, _monitor.TempCelsius, v => $"{v:F0}\u00B0");
        SetBar(Gpu0Bar, Gpu0Track, Gpu0Text, _monitor.Gpu0Percent, p => $"{p:F0}%");
        SetBar(Gpu1Bar, Gpu1Track, Gpu1Text, _monitor.Gpu1Percent, p => $"{p:F0}%");
        SyncGpuToolTip(Gpu0Label, _monitor.Gpu0Name);
        SyncGpuToolTip(Gpu1Label, _monitor.Gpu1Name);
        SetBar(MemBar, MemTrack, MemText, _monitor.MemoryPercent, p => $"{p:F0}%");
        SetBar(Disk0Bar, Disk0Track, Disk0Text, _monitor.Disk0Percent, p => $"{p:F0}%");
        SetBar(Disk1Bar, Disk1Track, Disk1Text, _monitor.Disk1Percent, p => $"{p:F0}%");
        SetBar(BatBar, BatTrack, BatText, _monitor.BatteryPercent,
            v => _monitor.BatteryCharging ? $"{v:F0}%+" : $"{v:F0}%");
        SetBar(RxBar, RxTrack, RxText, _monitor.RxBytesPerSec, FormatRate, MaxRateBytesPerSec);
        SetBar(TxBar, TxTrack, TxText, _monitor.TxBytesPerSec, FormatRate, MaxRateBytesPerSec);
    }

    /// <summary>按启动时解析出的盘符设置两行磁盘标签（如 DSKC/DSKD；无盘的行显示 DSK-）。</summary>
    private void ApplyHardwareLabels()
    {
        Disk0Label.Text = _monitor.Drive0Letter is string l0 ? $"DSK{l0}" : "DSK-";
        Disk1Label.Text = _monitor.Drive1Letter is string l1 ? $"DSK{l1}" : "DSK-";
    }

    /// <summary>GPU 名称就绪后在标签上挂悬停提示（如 "NVIDIA GeForce RTX 5060 Laptop GPU"）；仅变化时赋值。</summary>
    private static void SyncGpuToolTip(TextBlock label, string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (label.ToolTip is not string s || s != name) label.ToolTip = name;
    }

    // ---------- 指标显示/隐藏 ----------

    /// <summary>注册全部指标行及其菜单文字（磁盘行名称随实际盘符变化）。</summary>
    private void BuildMetricRegistry()
    {
        _metrics.Clear();
        _metrics.Add(new MetricDef("cpu", "CPU 占用", CpuRow));
        _metrics.Add(new MetricDef("tmp", "温度 (TMP)", TempRow));
        _metrics.Add(new MetricDef("gpu0", "GPU0 占用", Gpu0Row));
        _metrics.Add(new MetricDef("gpu1", "GPU1 占用", Gpu1Row));
        _metrics.Add(new MetricDef("mem", "内存 (MEM)", MemRow));
        _metrics.Add(new MetricDef("disk0", $"磁盘 ({Disk0Label.Text})", Disk0Row));
        _metrics.Add(new MetricDef("disk1", $"磁盘 ({Disk1Label.Text})", Disk1Row));
        _metrics.Add(new MetricDef("bat", "电池 (BAT)", BatRow));
        _metrics.Add(new MetricDef("rx", "下载 (RX)", RxRow));
        _metrics.Add(new MetricDef("tx", "上传 (TX)", TxRow));
    }

    /// <summary>从 settings.json 初始化可见指标集合；空/无效时回退为全部显示。</summary>
    private void InitVisibleMetrics()
    {
        var configured = _settings.VisibleMetrics;
        if (configured is not null && configured.Length > 0)
        {
            foreach (var id in configured)
                if (_metrics.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
                    _visibleMetrics.Add(id);

            if (_visibleMetrics.Count == 0)
                foreach (var m in _metrics) _visibleMetrics.Add(m.Id);
        }
        else
        {
            foreach (var m in _metrics) _visibleMetrics.Add(m.Id);
        }
    }

    /// <summary>折叠/展开指标行。</summary>
    private void ApplyMetricVisibility()
    {
        foreach (var m in _metrics)
            m.Row.Visibility = _visibleMetrics.Contains(m.Id) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>切换某一指标的显示/隐藏；最后一项不允许取消，静默忽略。</summary>
    private void ToggleMetric(string id)
    {
        if (_visibleMetrics.Contains(id) && _visibleMetrics.Count == 1)
            return;

        if (_visibleMetrics.Contains(id)) _visibleMetrics.Remove(id);
        else _visibleMetrics.Add(id);

        ApplyMetricVisibility();
        PersistVisibleMetrics();
    }

    /// <summary>将当前可见指标写回设置；全部显示时存 null，保持 settings.json 简洁。</summary>
    private void PersistVisibleMetrics()
    {
        _settings.VisibleMetrics = _metrics.Count(m => _visibleMetrics.Contains(m.Id)) == _metrics.Count
            ? null
            : _metrics.Where(m => _visibleMetrics.Contains(m.Id)).Select(m => m.Id).ToArray();
    }

    /// <summary>构建"显示指标"子菜单（窗口与托盘共用），打开时同步勾选。</summary>
    public MenuItem CreateMetricsMenu()
    {
        var parent = new MenuItem { Header = "显示指标" };
        foreach (var m in _metrics)
        {
            var item = new MenuItem { Header = m.Name, IsCheckable = true, StaysOpenOnClick = true, Tag = m.Id };
            item.Click += (_, _) =>
            {
                ToggleMetric(m.Id);
                item.IsChecked = _visibleMetrics.Contains(m.Id);
            };
            parent.Items.Add(item);
        }
        parent.SubmenuOpened += (_, _) =>
        {
            foreach (var child in parent.Items.OfType<MenuItem>())
                child.IsChecked = child.Tag is string id && _visibleMetrics.Contains(id);
        };
        return parent;
    }

    /// <summary>更新一根进度长条：value 为 0~max（或 null/负数表示不可用）。</summary>
    private static void SetBar(System.Windows.Shapes.Rectangle bar, Grid track, TextBlock text,
        double? value, Func<double, string> format, double max = 100)
    {
        if (value is not double v || v < 0 || max <= 0)
        {
            bar.Width = 0;
            text.Text = "--";
            return;
        }
        double ratio = Math.Clamp(v, 0, max) / max;
        bar.Width = ratio * track.ActualWidth;
        text.Text = format(v);
    }

    /// <summary>网速进度条满格按 100Mbps 计（单位：字节/秒）。</summary>
    private const double MaxRateBytesPerSec = 100.0 * 1000 * 1000 / 8;

    /// <summary>字节速率自适应格式：980B / 340K / 4.5M。</summary>
    private static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024) return $"{bytesPerSec / (1024.0 * 1024.0):F1}M";
        if (bytesPerSec >= 1024) return $"{bytesPerSec / 1024.0:F0}K";
        return $"{bytesPerSec:F0}B";
    }

    // ---------- 交互：拖动 / 右键菜单 ----------

    private void Root_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            ClampWindowToScreen();
        }
    }

    /// <summary>拖拽结束后将窗口限制在虚拟屏幕范围内，防止小窗口被拖到屏外丢失。</summary>
    private void ClampWindowToScreen()
    {
        var minX = SystemParameters.VirtualScreenLeft;
        var minY = SystemParameters.VirtualScreenTop;
        var maxX = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth;
        var maxY = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - ActualHeight;
        Left = Math.Clamp(Left, minX, Math.Max(minX, maxX));
        Top = Math.Clamp(Top, minY, Math.Max(minY, maxY));
    }

    private void Root_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        Root.ContextMenu.IsOpen = true;
        RefreshMenuChecks();
    }

    private MenuItem _menuTopmost = null!;
    private MenuItem _menuAutoStart = null!;
    private MenuItem _menuClickThrough = null!;

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        _menuTopmost = new MenuItem { Header = "窗口置顶", IsCheckable = true };
        _menuTopmost.Click += (_, _) => ToggleTopmost();

        _menuAutoStart = new MenuItem { Header = "开机自启", IsCheckable = true };
        _menuAutoStart.Click += (_, _) => ToggleAutoStart();

        _menuClickThrough = new MenuItem { Header = "点击穿透", IsCheckable = true };
        _menuClickThrough.Click += (_, _) => ToggleClickThrough();

        var reset = new MenuItem { Header = "复位到屏幕右上角" };
        reset.Click += (_, _) => ResetPosition();

        var hideWindow = new MenuItem { Header = "隐藏窗口" };
        hideWindow.Click += (_, _) => ToggleWindowVisibility();

        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => OnRequestExit?.Invoke();

        var metricsMenu = CreateMetricsMenu();
        var opacityMenu = CreateOpacityMenu(SetOpacityLevel);

        menu.Items.Add(_menuTopmost);
        menu.Items.Add(_menuAutoStart);
        menu.Items.Add(_menuClickThrough);
        menu.Items.Add(metricsMenu);
        menu.Items.Add(opacityMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(reset);
        menu.Items.Add(hideWindow);
        menu.Items.Add(exit);
        return menu;
    }

    private void RefreshMenuChecks()
    {
        if (Root.ContextMenu is null) return;
        _menuTopmost.IsChecked = IsTopmost;
        _menuAutoStart.IsChecked = IsAutoStart;
        _menuClickThrough.IsChecked = IsClickThrough;
    }

    public void ToggleTopmost()
    {
        IsTopmost = !IsTopmost;
        Topmost = IsTopmost;
        RefreshMenuChecks();
        _settings.Topmost = IsTopmost;
    }

    public void ToggleAutoStart()
    {
        IsAutoStart = !IsAutoStart;
        SetAutoStart(IsAutoStart);
        RefreshMenuChecks();
        _settings.AutoStart = IsAutoStart;
    }

    // ---------- 窗口透明度 ----------

    private static readonly double[] OpacityLevels = { 1.0, 0.85, 0.70, 0.55, 0.40 };

    /// <summary>构建"窗口透明度"子菜单（窗口与托盘菜单共用），打开子菜单时同步当前档位勾选。</summary>
    public MenuItem CreateOpacityMenu(Action<double> apply)
    {
        var parent = new MenuItem { Header = "窗口透明度" };
        foreach (var level in OpacityLevels)
        {
            var item = new MenuItem { Header = $"{(int)(level * 100)}%", IsCheckable = true, Tag = level };
            item.Click += (_, _) => apply(level);
            parent.Items.Add(item);
        }
        parent.SubmenuOpened += (_, _) =>
        {
            foreach (var child in parent.Items.OfType<MenuItem>())
                child.IsChecked = child.Tag is double l && Math.Abs(l - Opacity) < 0.001;
        };
        return parent;
    }

    public void SetOpacityLevel(double level)
    {
        Opacity = Math.Clamp(level, 0.4, 1.0);
        _settings.Opacity = Opacity;
    }

    /// <summary>切换点击穿透。开启后窗口不响应鼠标（不能拖动/右键），只能从托盘菜单关闭。</summary>
    public void ToggleClickThrough()
    {
        IsClickThrough = !IsClickThrough;
        ApplyClickThrough();
        RefreshMenuChecks();
        _settings.ClickThrough = IsClickThrough;
    }

    private void ApplyClickThrough() => WindowNative.SetClickThrough(this, IsClickThrough);

    /// <summary>切换窗口显示/隐藏；隐藏时程序继续驻留托盘，后台监控不中断。</summary>
    public void ToggleWindowVisibility()
    {
        if (IsWindowVisible)
        {
            Hide();
            IsWindowVisible = false;
        }
        else
        {
            Show();
            Activate();
            IsWindowVisible = true;
        }
    }

    // ---------- 开机自启（当前用户注册表 Run 项） ----------

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MiniMonitor";

    private static bool RegistryAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string v && v.Length > 0;
        }
        catch { return false; }
    }

    private static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enable)
        {
            string exe = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(exe))
                key.SetValue(RunValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    // ---------- 窗口位置 ----------

    private void ApplyStartupPosition()
    {
        if (_settings.X is double x && _settings.Y is double y && IsOnScreen(x, y))
        {
            Left = x;
            Top = y;
            return;
        }

        ResetPosition();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwndSource = (HwndSource?)PresentationSource.FromVisual(this);
        if (_hwndSource != null)
        {
            _hwndSource.AddHook(HotkeyHook);
            // Ctrl+Alt+M 全局热键：显示/隐藏窗口；被其他程序占用时静默失效
            _hotkeyRegistered = WindowNative.RegisterHotKey(_hwndSource.Handle,
                WindowNative.HotkeyId,
                WindowNative.MOD_CONTROL | WindowNative.MOD_ALT | WindowNative.MOD_NOREPEAT,
                WindowNative.VK_M);
        }

        // 句柄创建后才能改扩展样式：恢复上次保存的点击穿透状态
        if (IsClickThrough) ApplyClickThrough();
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WindowNative.WM_HOTKEY && wParam.ToInt32() == WindowNative.HotkeyId)
        {
            ToggleWindowVisibility();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void ResetPosition()
    {
        var wa = SystemParameters.WorkArea;
        // 注意：必须在窗口 Show 之后调用，否则 ActualWidth/ActualHeight 为 0；
        // 这里使用显式设置的 Width/Height，保证左上角始终落在屏幕内
        Left = Math.Max(wa.Left, wa.Right - Width - 24);
        Top = Math.Max(wa.Top, wa.Top + 24);
    }

    /// <summary>窗口显示后校验是否与任一显示器有交集，没有则复位到屏幕右上角。</summary>
    private void EnsureOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && !DisplayInfo.IsWindowOnAnyScreen(hwnd))
            ResetPosition();
    }

    private static bool IsOnScreen(double x, double y)
    {
        return DisplayInfo.IsPointOnAnyScreen(x, y);
    }

    // ---------- 设置持久化 ----------

    public void SaveSettings()
    {
        _settings.X = Left;
        _settings.Y = Top;
        _settings.Topmost = IsTopmost;
        _settings.AutoStart = IsAutoStart;
        _settings.ClickThrough = IsClickThrough;
        _settings.Opacity = Opacity;
        AppSettings.Save(_settings);
    }
}
