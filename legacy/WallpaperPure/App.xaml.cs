using System.Windows;
using System.Windows.Controls;
using WallpaperPure.Native;

namespace WallpaperPure;

public partial class App : Application
{
    private TrayIcon? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _window = new MainWindow();
        _window.Show();
        _tray = CreateTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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

        var tray = new TrayIcon("WallpaperPure - 一键隐藏/显示桌面图标", hicon)
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
