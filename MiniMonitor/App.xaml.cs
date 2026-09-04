using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using MiniMonitor.Monitor;
using MiniMonitor.Native;

namespace MiniMonitor;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private TrayIcon? _tray;
    private MainWindow? _main;
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

        _mutex = new Mutex(true, @"Local\MiniMonitor_Singleton", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("MiniMonitor 已经在运行了。\n\n请到系统托盘找到它的图标。",
                "MiniMonitor", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _main = new MainWindow();
        _main.Show();
        _main.OnRequestExit += ExitApp;

        SetupTray();
    }

    private MenuItem _trayTopmost = null!;
    private MenuItem _trayAutoStart = null!;

    private void SetupTray()
    {
        _tray = new TrayIcon("MiniMonitor - 性能监控", CreateTrayIconHandle())
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

        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitApp();

        menu.Items.Add(_trayTopmost);
        menu.Items.Add(_trayAutoStart);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        // 每次打开菜单时同步勾选状态
        menu.Opened += (_, _) =>
        {
            _trayTopmost.IsChecked = _main?.IsTopmost ?? false;
            _trayAutoStart.IsChecked = _main?.IsAutoStart ?? false;
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
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // ---------- 崩溃日志 ----------

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniMonitor");
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
