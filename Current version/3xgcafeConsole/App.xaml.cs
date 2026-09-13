using System;
using System.Threading;
using System.Windows;

namespace ThreeXGCafeConsole;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\3xgcafe-Console_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("3xgcafe Console 已在运行。", "3xgcafe Console",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 面板没有 StartupUri，必须在此显式创建并显示主窗口
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
