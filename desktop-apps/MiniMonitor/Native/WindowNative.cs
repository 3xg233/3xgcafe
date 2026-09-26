using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MiniMonitor.Native;

/// <summary>
/// 窗口级 Win32 封装：点击穿透（WS_EX_TRANSPARENT）与全局热键（RegisterHotKey）。
/// WPF 的 AllowsTransparency 窗口本就是分层窗口，追加 WS_EX_TRANSPARENT 后鼠标事件穿透到下层窗口。
/// </summary>
public static class WindowNative
{
    // ---------- 点击穿透 ----------

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT_BIT = 0x00000020L;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>切换窗口点击穿透。开启后窗口不响应任何鼠标操作（需从托盘菜单关闭）。</summary>
    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style = enabled ? style | WS_EX_TRANSPARENT_BIT : style & ~WS_EX_TRANSPARENT_BIT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
        // 通知窗口管理器按新样式重算非客户区/命中测试
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    // ---------- 全局热键 ----------

    public const int HotkeyId = 0x4D01;
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_M = 0x4D;

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}