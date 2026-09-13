using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using MiniMonitor.Ipc;
using MiniMonitor.Monitor;
using MiniMonitor.Native;

namespace MiniMonitor;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private TrayIcon? _tray;
    private MainWindow? _main;
    private ToolIpcServer? _ipc;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain", (Exception)args.ExceptionObject);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher", args.Exception);
            args.Handled = true; // 悬浮监控器尽量不闪退
        };

        _mutex = new Mutex(true, @"Local\3xgcafe-Monitor_Singleton", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("3xgcafe Monitor 已经在运行了。\n\n请到系统托盘找到它的图标。",
                "3xgcafe Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _main = new MainWindow();
        _main.Show();
        _main.OnRequestExit += ExitApp;

        SetupTray();

        // 3xgcafe Console 管理通道（命名管道 3xgcafe-Monitor）
        _ipc = new ToolIpcServer("Monitor", HandleIpc);
    }

    /// <summary>管理面板指令：status / show / toggle_topmost / quit。</summary>
    private string HandleIpc(string cmd, IReadOnlyDictionary<string, string> args)
    {
        switch (cmd)
        {
            case "status":
            {
                SysMonitor? m = _main?.Monitor;
                if (m == null)
                    return ToolIpc.Response(false, "error", "窗口尚未就绪", null);

                var data = new Dictionary<string, string>
                {
                    ["cpu"] = $"{Math.Max(0, m.CpuPercent):F0}",
                    ["mem"] = $"{Math.Max(0, m.MemoryPercent):F0}",
                    ["temp"] = $"{m.TempCelsius:F0}",
                    ["gpu0"] = $"{m.Gpu0Percent:F0}",
                    ["gpu1"] = $"{m.Gpu1Percent:F0}",
                    ["disk0"] = $"{m.Disk0Percent:F0}",
                    ["disk1"] = $"{m.Disk1Percent:F0}",
                    ["battery"] = $"{m.BatteryPercent:F0}",
                    ["topmost"] = (_main?.IsTopmost ?? false) ? "1" : "0",
                    ["autostart"] = (_main?.IsAutoStart ?? false) ? "1" : "0"
                };
                string detail = $"CPU {Math.Max(0, m.CpuPercent):F0}% · 内存 {Math.Max(0, m.MemoryPercent):F0}% · {m.TempCelsius:F0}°C";
                return ToolIpc.Response(true, "running", detail, data);
            }

            case "show":
                Dispatcher.BeginInvoke(new Action(BringToFront));
                return ToolIpc.Response(true, "running", "已唤起监控窗口", null);

            case "toggle_topmost":
                Dispatcher.BeginInvoke(new Action(() => _main?.ToggleTopmost()));
                return ToolIpc.Response(true, "running", "已切换窗口置顶", null);

            case "quit":
                Dispatcher.BeginInvoke(new Action(ExitApp));
                return ToolIpc.Response(true, "stopping", "正在退出", null);

            default:
                return ToolIpc.Response(false, "error", "未知命令: " + cmd, null);
        }
    }

    private MenuItem _trayTopmost = null!;
    private MenuItem _trayAutoStart = null!;
    private MenuItem _trayToggleVisibility = null!;
    private MenuItem _trayClickThrough = null!;

    private void SetupTray()
    {
        _tray = new TrayIcon("3xgcafe Monitor - 性能监控", CreateTrayIconHandle())
        {
            ContextMenu = BuildTrayMenu(),
        };
        _tray.DoubleClick += () => BringToFront();
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();

        _trayTopmost = new MenuItem { Header = "窗口置顶", IsCheckable = true };
        _trayTopmost.Click += (_, _) => _main?.ToggleTopmost();

        _trayAutoStart = new MenuItem { Header = "开机自启", IsCheckable = true };
        _trayAutoStart.Click += (_, _) => _main?.ToggleAutoStart();

        _trayClickThrough = new MenuItem { Header = "点击穿透", IsCheckable = true };
        _trayClickThrough.Click += (_, _) => _main?.ToggleClickThrough();

        _trayToggleVisibility = new MenuItem { Header = "隐藏窗口" };
        _trayToggleVisibility.Click += (_, _) => _main?.ToggleWindowVisibility();

        var reset = new MenuItem { Header = "复位到屏幕右上角" };
        reset.Click += (_, _) => _main?.ResetPosition();

        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitApp();

        menu.Items.Add(_trayToggleVisibility);
        menu.Items.Add(new Separator());
        menu.Items.Add(_trayTopmost);
        menu.Items.Add(_trayAutoStart);
        menu.Items.Add(_trayClickThrough);
        if (_main != null)
        {
            menu.Items.Add(_main.CreateMetricsMenu());
            menu.Items.Add(_main.CreateOpacityMenu(v => _main.SetOpacityLevel(v)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(reset);
        menu.Items.Add(exit);

        // 每次打开菜单时同步勾选状态
        menu.Opened += (_, _) =>
        {
            _trayToggleVisibility.Header = (_main?.IsWindowVisible ?? true) ? "隐藏窗口" : "显示窗口";
            _trayTopmost.IsChecked = _main?.IsTopmost ?? false;
            _trayAutoStart.IsChecked = _main?.IsAutoStart ?? false;
            _trayClickThrough.IsChecked = _main?.IsClickThrough ?? false;
        };
        return menu;
    }

    private void BringToFront()
    {
        // 仅唤起窗口，不改变置顶状态（默认不置顶，允许被其他窗口覆盖）
        _main?.Activate();
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;

        _tray?.Dispose();
        _tray = null;
        _main?.SaveSettings();
        _main?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ipc?.Dispose();
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // ---------- 崩溃日志 ----------

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "3xgcafe", "Monitor");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n\n");
        }
        catch { }
    }

    // ---------- 托盘图标：黑色圆角方块 + 白色折线，与窗口风格一致 ----------

    /// <summary>生成 16x16 托盘图标 HIcon，调用方（TrayIcon）负责 DestroyIcon。</summary>
    private static IntPtr CreateTrayIconHandle()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Black);
            using var pen = new Pen(Color.White, 1.4f);
            g.DrawRectangle(pen, 1, 1, 13, 13);
            using var linePen = new Pen(Color.White, 1.2f);
            g.DrawLines(linePen, new[]
            {
                new System.Drawing.Point(4, 10),
                new System.Drawing.Point(7, 6),
                new System.Drawing.Point(9, 9),
                new System.Drawing.Point(13, 4),
            });
        }
        return bmp.GetHicon();
    }
}
