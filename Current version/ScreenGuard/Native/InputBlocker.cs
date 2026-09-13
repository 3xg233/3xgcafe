using System;
using System.Runtime.InteropServices;

namespace ScreenGuard.Native;

/// <summary>
/// 锁屏期间的输入封锁：安装 WH_KEYBOARD_LL / WH_MOUSE_LL 全局低级钩子。
///
/// 规则：
/// - 前台窗口属于本进程（锁屏窗）→ 放行普通按键，仅拦截 Win / Tab / Esc / F4
///   （这四类可配合 Alt/Ctrl 用于切换窗口、打开开始菜单、关闭窗口）。
/// - 前台窗口不属于本进程 → 键盘与鼠标全部吞掉，防止操作背后的程序。
///
/// 已知限制：Ctrl+Alt+Del 是系统安全注意序列（SAS），由 winlogon 处理，
/// 用户态程序无法拦截（这也是忘记密码时的官方逃生通道）。
/// </summary>
public sealed class InputBlocker : IDisposable
{
    private static readonly uint[] BlockedKeys =
    {
        0x5B, // VK_LWIN
        0x5C, // VK_RWIN
        0x09, // VK_TAB
        0x1B, // VK_ESCAPE
        0x73, // VK_F4
    };

    private readonly NativeMethods.LowLevelProc _keyboardProc;
    private readonly NativeMethods.LowLevelProc _mouseProc;

    private IntPtr _keyboardHook = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;

    public InputBlocker()
    {
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    public bool IsActive { get; private set; }

    /// <summary>由锁屏窗口注入：当前前台窗口是否属于本进程。</summary>
    public Func<bool>? IsOwnWindowForeground { get; set; }

    public void Start()
    {
        if (IsActive)
            return;

        IntPtr module = NativeMethods.GetModuleHandleW(null);
        _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
        IsActive = true;
    }

    public void Stop()
    {
        if (!IsActive)
            return;

        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        IsActive = false;
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsActive && !ShouldAllowKey(lParam))
            return 1; // 吞掉该按键

        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsActive && IsOwnWindowForeground?.Invoke() != true)
            return 1; // 前台不是锁屏窗时，鼠标全部吞掉

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool ShouldAllowKey(IntPtr lParam)
    {
        // 前台不是锁屏窗 → 一律拦截
        if (IsOwnWindowForeground?.Invoke() != true)
            return false;

        var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        foreach (uint blocked in BlockedKeys)
        {
            if (info.vkCode == blocked)
                return false;
        }
        return true;
    }

    public void Dispose() => Stop();
}
