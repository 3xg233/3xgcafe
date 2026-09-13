using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScreenGuard.Ipc;
using ScreenGuard.Native;

namespace ScreenGuard;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppSettings _settings = new();
    private TrayIcon? _tray;
    private ButtonWindow? _button;
    private ToolIpcServer? _ipc;
    private IdleMonitor? _idle;
    private InputBlocker? _blocker;
    private LockWindow? _lockWindow;
    private int _tickCounter;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\3xgcafe-Guard_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("3xgcafe Guard 已在运行，可在任务栏托盘区查看图标。", "3xgcafe Guard",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();

        // 未设置密码 → 先强制完成首次设置
        if (!_settings.HasPassword)
        {
            var setup = new SettingsWindow(_settings, firstRun: true);
            if (setup.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        _blocker = new InputBlocker();

        _idle = new IdleMonitor { IdleMinutes = _settings.IdleMinutes };
        _idle.Tick += OnIdleTick;
        _idle.IdleThresholdReached += LockNow;
        _idle.Start();

        _tray = CreateTrayIcon();

        // 桌面悬浮按钮：左键单击=立即锁定，右键=操控菜单
        _button = new ButtonWindow();
        _button.LockRequested += LockNow;
        _button.MenuRequested += ShowMainMenu;
        _button.Show();

        UpdateTrayTooltip();

        // 3xgcafe Console 管理通道（命名管道 3xgcafe-ScreenGuard）
        _ipc = new ToolIpcServer("Guard", HandleIpc);
    }

    /// <summary>管理面板指令：status / lock / pause / resume / quit。</summary>
    private string HandleIpc(string cmd, IReadOnlyDictionary<string, string> args)
    {
        switch (cmd)
        {
            case "status":
            {
                bool locked = _lockWindow != null;
                bool paused = _idle?.IsPaused ?? false;
                var data = new Dictionary<string, string>
                {
                    ["locked"] = locked ? "1" : "0",
                    ["paused"] = paused ? "1" : "0",
                    ["autostart"] = AutoStart.IsEnabled() ? "1" : "0"
                };

                if (locked)
                    return ToolIpc.Response(true, "locked", "已锁定：等待输入密码", data);

                if (paused && _idle != null)
                    return ToolIpc.Response(true, "paused", $"监控已暂停，剩余 {Format(_idle.PauseRemaining)}", data);

                if (_idle != null)
                    return ToolIpc.Response(true, "running", $"保护中，{Format(_idle.TimeUntilLock)} 后锁定", data);

                return ToolIpc.Response(false, "error", "监控未启动", null);
            }

            case "lock":
                Dispatcher.BeginInvoke(new Action(LockNow));
                return ToolIpc.Response(true, "running", "已发起立即锁定", null);

            case "pause":
            {
                int minutes = ToolIpc.TryGetInt(args, "minutes", out int m) ? Math.Clamp(m, 1, 480) : 30;
                Dispatcher.BeginInvoke(new Action(() => Pause(TimeSpan.FromMinutes(minutes))));
                return ToolIpc.Response(true, "paused", $"已暂停监控 {minutes} 分钟", null);
            }

            case "resume":
                Dispatcher.BeginInvoke(new Action(ResumeMonitoring));
                return ToolIpc.Response(true, "running", "已恢复监控", null);

            case "quit":
                Dispatcher.BeginInvoke(new Action(Shutdown));
                return ToolIpc.Response(true, "stopping", "正在退出", null);

            default:
                return ToolIpc.Response(false, "error", "未知命令: " + cmd, null);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ipc?.Dispose();
        _blocker?.Stop();
        _blocker?.Dispose();
        _idle?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    // ---------------- 锁定 / 解锁 ----------------

    /// <summary>立即锁定（空闲触发或托盘菜单手动触发）。</summary>
    private void LockNow()
    {
        if (_lockWindow != null || _blocker == null)
            return;

        _idle?.Stop();

        // 快照作为兜底必须在锁屏窗出现之前抓（否则会拍到自己的锁屏界面）；
        // 正常情况下走 DWM 实时模糊，快照只在亚克力不可用时显示
        ImageSource? backdrop = _settings.BlurBackground ? LockBackground.CreateBlurred() : null;

        var window = new LockWindow(_settings, _blocker, backdrop);
        window.Unlocked += OnUnlocked;
        _lockWindow = window;

        window.Show();
        window.Activate();

        // 锁屏窗已就位，同一 UI 回合内立刻封锁输入
        _blocker.Start();

        window.Focus();
        UpdateTrayTooltip();
    }

    private void OnUnlocked()
    {
        _blocker?.Stop();

        if (_lockWindow != null)
        {
            LockWindow window = _lockWindow;
            _lockWindow = null;
            window.Unlocked -= OnUnlocked;
            window.Close();
        }

        _idle?.Restart();
        UpdateTrayTooltip();
    }

    // ---------------- 托盘 ----------------

    private TrayIcon CreateTrayIcon()
    {
        IntPtr hicon = TrayIconHelper.CreateIconHandle();

        return new TrayIcon("3xgcafe Guard - 空闲自动锁定", hicon)
        {
            ContextMenu = BuildMenu()
        };
    }

    /// <summary>
    /// 构建主菜单（托盘与桌面按钮共用）。每次实时构建，
    /// 保证"开机自启"勾选态、"临时关闭监控"等反映当前真实状态。
    /// </summary>
    private ContextMenu BuildMenu()
    {
        var pauseMenu = new MenuItem { Header = "临时关闭监控" };
        pauseMenu.Items.Add(new MenuItem { Header = "15 分钟", Command = new RelayCommand(() => Pause(TimeSpan.FromMinutes(15))) });
        pauseMenu.Items.Add(new MenuItem { Header = "30 分钟", Command = new RelayCommand(() => Pause(TimeSpan.FromMinutes(30))) });
        pauseMenu.Items.Add(new MenuItem { Header = "1 小时", Command = new RelayCommand(() => Pause(TimeSpan.FromHours(1))) });
        pauseMenu.Items.Add(new MenuItem { Header = "2 小时", Command = new RelayCommand(() => Pause(TimeSpan.FromHours(2))) });
        pauseMenu.Items.Add(new Separator());
        pauseMenu.Items.Add(new MenuItem { Header = "恢复监控", Command = new RelayCommand(ResumeMonitoring) });

        var autoStartItem = new MenuItem
        {
            Header = "开机自启",
            IsCheckable = true,
            IsChecked = AutoStart.IsEnabled()
        };
        autoStartItem.Click += (_, _) => AutoStart.SetEnabled(autoStartItem.IsChecked);

        return new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = "立即锁定", Command = new RelayCommand(LockNow) },
                pauseMenu,
                new Separator(),
                new MenuItem { Header = "设置...", Command = new RelayCommand(OpenSettings) },
                autoStartItem,
                new Separator(),
                new MenuItem { Header = "退出", Command = new RelayCommand(() => Shutdown()) }
            }
        };
    }

    private void ShowMainMenu(ButtonWindow button)
    {
        button.OpenMenu(BuildMenu());
        UpdateTrayTooltip();
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(_settings, firstRun: false);
        if (window.ShowDialog() == true && _idle != null)
            _idle.IdleMinutes = _settings.IdleMinutes;

        UpdateTrayTooltip();
    }

    private void Pause(TimeSpan duration)
    {
        _idle?.Pause(duration);
        UpdateTrayTooltip();
    }

    private void ResumeMonitoring()
    {
        _idle?.Resume();
        UpdateTrayTooltip();
    }

    private void OnIdleTick()
    {
        // 托盘提示每 5 秒刷新一次即可，避免频繁调用 Shell_NotifyIcon
        _tickCounter++;
        if (_tickCounter % 5 == 0)
            UpdateTrayTooltip();
    }

    private void UpdateTrayTooltip()
    {
        if (_tray == null || _idle == null)
            return;

        if (_lockWindow != null)
        {
            _tray.SetTooltip("3xgcafe Guard - 已锁定，请输入密码");
            return;
        }

        if (_idle.IsPaused)
        {
            _tray.SetTooltip($"3xgcafe Guard - 监控已暂停（剩余 {Format(_idle.PauseRemaining)}）");
            return;
        }

        TimeSpan remain = _idle.TimeUntilLock;
        if (remain < TimeSpan.Zero)
            remain = TimeSpan.Zero;

        _tray.SetTooltip($"3xgcafe Guard - 保护中，{Format(remain)} 后锁定");
    }

    private static string Format(TimeSpan span)
    {
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
        if (span.TotalMinutes >= 1)
            return $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒";
        return $"{span.Seconds} 秒";
    }
}
