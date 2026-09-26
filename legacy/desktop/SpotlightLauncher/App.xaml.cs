using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using SpotlightLauncher.Native;

namespace SpotlightLauncher;

public partial class App : System.Windows.Application
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SpotlightLauncher";

    private Mutex? _mutex;
    private TrayIcon? _tray;
    private MainWindow? _main;
    private HwndSource? _hwndSource;
    private MenuItem? _trayAutoStart;
    private MenuItem? _trayKeepOpen;
    private bool _exiting;

    private const int HotKeyId = 1;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain", (Exception)args.ExceptionObject);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher", args.Exception);
            args.Handled = true;
        };

        _mutex = new Mutex(true, @"Local\SpotlightLauncher_Singleton", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("SpotlightLauncher 已在运行。",
                "SpotlightLauncher", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 开机自启：尊重用户持久化的偏好（首次运行默认开启；取消勾选后不再被静默恢复）
        SetAutoStart(LoadAutoStartPref());

        _main = new MainWindow();
        _main.KeepOnDeactivate = LoadKeepOnDeactivatePref(); // "失活时保持窗口"偏好

        SetupTray();

        // 注册全局热键：Ctrl + Alt + Space（不重复触发）
        RegisterHotKey();

        // 诊断/测试用：--no-autohide 时直接显示窗口，绕过热键
        if (Array.Exists(Environment.GetCommandLineArgs(),
                a => a.Equals("--no-autohide", StringComparison.OrdinalIgnoreCase)))
            _main!.ShowLauncher();
    }

    // ---------- 开机自启 ----------

    private static bool IsAutoStartEnabled()
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
        try
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
        catch { /* 注册表写失败不影响运行 */ }
    }

    // ---------- 开机自启偏好持久化（防止取消勾选后被静默恢复）----------

    private static string AutoStartSettingsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpotlightLauncher", "autostart.json");

    // ---------- 偏好文件（autostart.json 实际为偏好文件，含 AutoStart 与 KeepOnDeactivate 两键）----------

    private static Dictionary<string, bool> LoadPrefs()
    {
        try
        {
            if (File.Exists(AutoStartSettingsFile)
                && JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(AutoStartSettingsFile)) is { } doc)
                return doc;
        }
        catch { /* 配置损坏时按缺省处理 */ }
        return new Dictionary<string, bool>();
    }

    private static void SavePrefs(Dictionary<string, bool> prefs)
    {
        try
        {
            var dir = Path.GetDirectoryName(AutoStartSettingsFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(AutoStartSettingsFile, JsonSerializer.Serialize(prefs));
        }
        catch { /* 写失败不影响使用 */ }
    }

    private static bool LoadAutoStartPref()
    {
        var prefs = LoadPrefs();
        if (prefs.TryGetValue("AutoStart", out var v)) return v;
        // 首次运行（无该键）：默认开启，并落盘记住该偏好
        prefs["AutoStart"] = true;
        SavePrefs(prefs);
        return true;
    }

    /// <summary>"失活时保持窗口"偏好；缺键默认 false（保持原有自动隐藏行为）。</summary>
    private static bool LoadKeepOnDeactivatePref() =>
        LoadPrefs().TryGetValue("KeepOnDeactivate", out var v) && v;

    private void ToggleAutoStart()
    {
        bool next = !IsAutoStartEnabled();
        SetAutoStart(next);
        var prefs = LoadPrefs();
        prefs["AutoStart"] = next;
        SavePrefs(prefs);
        if (_trayAutoStart is not null) _trayAutoStart.IsChecked = next;
    }

    private void ToggleKeepOnDeactivate()
    {
        var prefs = LoadPrefs();
        bool next = !prefs.TryGetValue("KeepOnDeactivate", out var v) || !v;
        prefs["KeepOnDeactivate"] = next;
        SavePrefs(prefs);
        if (_main is not null) _main.KeepOnDeactivate = next;
        if (_trayKeepOpen is not null) _trayKeepOpen.IsChecked = next;
    }

    // ---------- 全局热键 ----------

    private void RegisterHotKey()
    {
        // 窗口从不主动显示，必须用 EnsureHandle 提前创建 HWND，否则 SourceInitialized 不会触发、热键无法注册
        var helper = new WindowInteropHelper(_main!);
        IntPtr handle = helper.EnsureHandle();

        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(WndProc);

        if (!NativeMethods.RegisterHotKey(handle, HotKeyId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
                NativeMethods.VK_SPACE))
        {
            _tray?.ShowBalloonTip(3000, "SpotlightLauncher",
                "全局热键 Ctrl+Alt+Space 注册失败（可能被其他程序占用）。可双击托盘图标打开。",
                TrayIcon.BalloonIcon.Warning);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            // 热键为可见性开关:可见则收起,隐藏则呼出
            if (_main is not null && _main.IsVisible)
                _main.HideLauncher();
            else
                _main?.ShowLauncher();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---------- 托盘 ----------

    private void SetupTray()
    {
        _tray = new TrayIcon("SpotlightLauncher - Ctrl+Alt+Space", CreateTrayIconHandle())
        {
            ContextMenu = BuildTrayMenu(),
        };
        _tray.DoubleClick += () => _main?.ShowLauncher();
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = "打开启动器" };
        openItem.Click += (_, _) => _main?.ShowLauncher();
        menu.Items.Add(openItem);

        _trayAutoStart = new MenuItem { Header = "开机自启", IsCheckable = true };
        _trayAutoStart.Click += (_, _) => ToggleAutoStart();
        menu.Items.Add(_trayAutoStart);

        _trayKeepOpen = new MenuItem { Header = "失活时保持窗口", IsCheckable = true };
        _trayKeepOpen.Click += (_, _) => ToggleKeepOnDeactivate();
        menu.Items.Add(_trayKeepOpen);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(exitItem);

        // 打开菜单时同步勾选状态
        menu.Opened += (_, _) =>
        {
            _trayAutoStart.IsChecked = IsAutoStartEnabled();
            _trayKeepOpen.IsChecked = _main?.KeepOnDeactivate ?? false;
        };
        return menu;
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;

        if (_hwndSource is not null && _main is not null)
        {
            IntPtr handle = new WindowInteropHelper(_main).Handle;
            NativeMethods.UnregisterHotKey(handle, HotKeyId);
        }

        _tray?.Dispose();
        _tray = null;
        _main?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // ---------- 日志 ----------

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotlightLauncher");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n\n");
        }
        catch { }
    }

    // ---------- 托盘图标：荧光黄放大镜 ----------

    /// <summary>生成 16x16 托盘图标 HIcon:荧光黄放大镜(与应用图标一致),调用方(TrayIcon)负责 DestroyIcon。</summary>
    private static IntPtr CreateTrayIconHandle()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(0x14, 0x15, 0x1D));
            // 放大镜(荧光黄,终末地装饰色 RGB 250,252,82):圆心 (7.4, 7.4),半径 3.8,手柄 45°
            using var pen = new Pen(Color.FromArgb(0xFF, 0xFA, 0xFC, 0x52), 1.5f);
            g.DrawEllipse(pen, 3.5f, 3.5f, 7.7f, 7.7f);
            g.DrawLine(pen, 10.1f, 10.1f, 12.5f, 12.5f);
        }
        return bmp.GetHicon();
    }
}
