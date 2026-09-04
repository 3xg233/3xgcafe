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

    private static bool LoadAutoStartPref()
    {
        try
        {
            if (File.Exists(AutoStartSettingsFile))
            {
                var json = File.ReadAllText(AutoStartSettingsFile);
                if (JsonSerializer.Deserialize<Dictionary<string, bool>>(json)
                    is { } doc && doc.TryGetValue("AutoStart", out var v))
                    return v;
            }
        }
        catch { /* 配置损坏时回退默认 */ }

        // 首次运行（无配置文件）：默认开启，并落盘记住该偏好
        SaveAutoStartPref(true);
        return true;
    }

    private static void SaveAutoStartPref(bool enable)
    {
        try
        {
            var dir = Path.GetDirectoryName(AutoStartSettingsFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(AutoStartSettingsFile,
                JsonSerializer.Serialize(new Dictionary<string, bool> { ["AutoStart"] = enable }));
        }
        catch { /* 写失败不影响使用 */ }
    }

    private void ToggleAutoStart()
    {
        bool next = !IsAutoStartEnabled();
        SetAutoStart(next);
        SaveAutoStartPref(next);
        if (_trayAutoStart is not null) _trayAutoStart.IsChecked = next;
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

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(exitItem);

        // 打开菜单时同步勾选状态
        menu.Opened += (_, _) => _trayAutoStart.IsChecked = IsAutoStartEnabled();
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

    // ---------- 托盘图标：黑白极简 ----------

    /// <summary>生成 16x16 托盘图标 HIcon，调用方（TrayIcon）负责 DestroyIcon。</summary>
    private static IntPtr CreateTrayIconHandle()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(0x14, 0x15, 0x1D));
            // 荧光黄绿放大镜（终末地装饰色 RGB 250,252,82）
            using var pen = new Pen(Color.FromArgb(0xFF, 0xFA, 0xFC, 0x52), 1.4f);
            g.DrawEllipse(pen, 2, 2, 8, 8);
            g.DrawLine(pen, 9, 9, 13, 13);
        }
        return bmp.GetHicon();
    }
}
