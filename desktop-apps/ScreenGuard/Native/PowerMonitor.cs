using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ScreenGuard.Native;

/// <summary>
/// 系统电源与显示状态监听。
/// 通过一个不可见的消息窗口接收 WM_POWERBROADCAST，并注册「控制台显示状态」通知：
///   - 系统即将睡眠（挂起） → <see cref="Suspending"/>
///   - 从睡眠恢复           → <see cref="Resumed"/>
///   - 显示器关闭 / 点亮     → <see cref="DisplayStateChanged"/>
/// 用途：睡眠或息屏时解除本工具的屏幕防护，避免唤醒后锁屏窗压住 Windows 自身的登录界面，
/// 导致用户无法用正常方式输入密码。
/// </summary>
internal sealed class PowerMonitor : IDisposable
{
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    /// <summary>控制台显示状态（开 / 关 / 变暗）的通知 GUID。</summary>
    private static readonly Guid GuidConsoleDisplayState =
        new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(
        IntPtr hRecipient, ref Guid powerSettingGuid, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    private readonly HwndSource _source;
    private IntPtr _displayNotify;

    /// <summary>系统即将睡眠（挂起）。</summary>
    public event Action? Suspending;

    /// <summary>系统从睡眠恢复。</summary>
    public event Action? Resumed;

    /// <summary>显示器状态变化：true = 已点亮，false = 已关闭 / 变暗。</summary>
    public event Action<bool>? DisplayStateChanged;

    public PowerMonitor()
    {
        var parameters = new HwndSourceParameters("3xgcafe-Guard-PowerWatcher")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0, // 不可见、无边框的消息窗口
            ParentWindow = IntPtr.Zero
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        Guid guid = GuidConsoleDisplayState;
        _displayNotify = RegisterPowerSettingNotification(
            _source.Handle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_POWERBROADCAST)
            return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case PBT_APMSUSPEND:
                handled = true;
                Suspending?.Invoke();
                break;

            case PBT_APMRESUMEAUTOMATIC:
            case PBT_APMRESUMESUSPEND:
                handled = true;
                Resumed?.Invoke();
                break;

            case PBT_POWERSETTINGCHANGE:
                handled = true;
                if (lParam != IntPtr.Zero)
                {
                    PowerBroadcastSetting setting =
                        Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
                    if (setting.PowerSetting == GuidConsoleDisplayState)
                    {
                        // Data: 0 = 关闭, 1 = 开启, 2 = 变暗（按"未点亮"处理）
                        DisplayStateChanged?.Invoke(setting.Data == 1);
                    }
                }
                break;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_displayNotify != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_displayNotify);
            _displayNotify = IntPtr.Zero;
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
