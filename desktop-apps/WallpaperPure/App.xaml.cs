using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using WallpaperPure.Ipc;
using WallpaperPure.Native;

namespace WallpaperPure;

public partial class App : Application
{
    private TrayIcon? _tray;
    private MainWindow? _window;
    private ToolIpcServer? _ipc;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _window = new MainWindow();
        _window.Show();
        _tray = CreateTrayIcon();

        // 3xgcafe Console 管理通道（命名管道 3xgcafe-Wallpaper）
        _ipc = new ToolIpcServer("Wallpaper", HandleIpc);
    }

    /// <summary>管理面板指令：status / toggle_icons / show_icons / hide_icons / quit。</summary>
    private string HandleIpc(string cmd, IReadOnlyDictionary<string, string> args)
    {
        switch (cmd)
        {
            case "status":
            {
                bool visible = NativeMethods.AreDesktopIconsVisible();
                var data = new Dictionary<string, string>
                {
                    ["iconsVisible"] = visible ? "1" : "0",
                    ["autostart"] = AutoStart.IsEnabled() ? "1" : "0"
                };
                return ToolIpc.Response(true, "running", visible ? "桌面图标：显示中" : "桌面图标：已隐藏", data);
            }

            case "toggle_icons":
                Dispatcher.BeginInvoke(new Action(() => _window?.IpcToggleIcons()));
                return ToolIpc.Response(true, "running", "已切换桌面图标显隐", null);

            case "show_icons":
                Dispatcher.BeginInvoke(new Action(() => _window?.IpcSetIconsVisible(true)));
                return ToolIpc.Response(true, "running", "已显示桌面图标", null);

            case "hide_icons":
                Dispatcher.BeginInvoke(new Action(() => _window?.IpcSetIconsVisible(false)));
                return ToolIpc.Response(true, "running", "已隐藏桌面图标", null);

            case "quit":
                Dispatcher.BeginInvoke(new Action(() => Current.Shutdown()));
                return ToolIpc.Response(true, "stopping", "正在退出", null);

            default:
                return ToolIpc.Response(false, "error", "未知命令: " + cmd, null);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ipc?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }

    private static TrayIcon CreateTrayIcon()
    {
        var hicon = TrayIconHelper.CreateIconHandle();

        // 开机自启勾选项：点击时 IsChecked 已由 MenuItem 自动翻转，据此同步注册表
        var autoStartItem = new MenuItem
        {
            Header = "开机自启",
            IsCheckable = true,
            IsChecked = AutoStart.IsEnabled()
        };
        autoStartItem.Click += (_, _) => AutoStart.SetEnabled(autoStartItem.IsChecked);

        var tray = new TrayIcon("3xgcafe Wallpaper - 一键隐藏/显示桌面图标", hicon)
        {
            ContextMenu = new ContextMenu
            {
                Items =
                {
                    autoStartItem,
                    new MenuItem
                    {
                        Header = "退出",
                        Command = new RelayCommand(() => Current.Shutdown())
                    }
                }
            }
        };
        return tray;
    }
}
