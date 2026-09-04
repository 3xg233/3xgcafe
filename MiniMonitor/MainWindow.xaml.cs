using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Shapes;
using Microsoft.Win32;
using MiniMonitor.Monitor;
using MiniMonitor.Native;

namespace MiniMonitor;

public partial class MainWindow : Window
{
    private readonly SysMonitor _monitor = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _savePositionTimer;
    private readonly AppSettings _settings;

    public event Action? OnRequestExit;

    public bool IsTopmost { get; private set; }
    public bool IsAutoStart { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Root.ContextMenu = BuildContextMenu();

        _settings = AppSettings.Load();

        IsTopmost = _settings.Topmost;
        IsAutoStart = RegistryAutoStartEnabled();
        Topmost = IsTopmost;

        ApplyStartupPosition();
        RefreshMenuChecks();

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
        SetBar(MemBar, MemTrack, MemText, _monitor.MemoryPercent, p => $"{p:F0}%");
        SetBar(DiskCBar, DiskCTrack, DiskCText, _monitor.DiskCPercent, p => $"{p:F0}%");
        SetBar(DiskDBar, DiskDTrack, DiskDText, _monitor.DiskDPercent, p => $"{p:F0}%");
    }

    /// <summary>更新一根进度长条：value 为 0~100（或 null/负数表示不可用）。</summary>
    private static void SetBar(System.Windows.Shapes.Rectangle bar, Grid track, TextBlock text, double? value, Func<double, string> format)
    {
        if (value is not double v || v < 0)
        {
            bar.Width = 0;
            text.Text = "--";
            return;
        }
        double ratio = Math.Clamp(v, 0, 100) / 100.0;
        bar.Width = ratio * track.ActualWidth;
        text.Text = format(v);
    }

    // ---------- 交互：拖动 / 右键菜单 ----------

    private void Root_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Root_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        Root.ContextMenu.IsOpen = true;
        RefreshMenuChecks();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var topmost = new MenuItem { Header = "窗口置顶", IsCheckable = true };
        topmost.Click += (_, _) => ToggleTopmost();

        var autoStart = new MenuItem { Header = "开机自启", IsCheckable = true };
        autoStart.Click += (_, _) => ToggleAutoStart();

        var reset = new MenuItem { Header = "复位到屏幕右上角" };
        reset.Click += (_, _) => ResetPosition();

        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => OnRequestExit?.Invoke();

        menu.Items.Add(topmost);
        menu.Items.Add(autoStart);
        menu.Items.Add(new Separator());
        menu.Items.Add(reset);
        menu.Items.Add(exit);
        return menu;
    }

    private void RefreshMenuChecks()
    {
        if (Root.ContextMenu is null) return;
        var items = Root.ContextMenu.Items;
        if (items.Count > 0) ((MenuItem)items[0]).IsChecked = IsTopmost;
        if (items.Count > 1) ((MenuItem)items[1]).IsChecked = IsAutoStart;
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

    private void ResetPosition()
    {
        var wa = SystemParameters.WorkArea;
        // 注意：必须在窗口 Show 之后调用，否则 ActualWidth/ActualHeight 为 0；
        // 这里使用显式设置的 Width/Height，保证左上角始终落在屏幕内
        Left = Math.Max(wa.Left, wa.Right - Width - 24);
        Top = Math.Max(wa.Top, wa.Top + 24);
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
        AppSettings.Save(_settings);
    }
}
