using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace MiniMonitor.Native;

/// <summary>
/// 屏幕信息与鼠标位置 Win32 封装，替代 System.Windows.Forms.Screen / Cursor。
/// </summary>
public static class DisplayInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint MONITOR_DEFAULTTONULL = 0x00000000;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
        EnumMonitorsDelegate lpfnEnum, IntPtr dwData);

    private delegate bool EnumMonitorsDelegate(IntPtr hMonitor, IntPtr hdcMonitor,
        ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(ref POINT lpPoint);

    private static Rect ToRect(RECT r) =>
        new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    /// <summary>鼠标当前位置（屏幕坐标）。</summary>
    public static Point GetCursorPosition()
    {
        POINT p = default;
        GetCursorPos(ref p);
        return new Point(p.X, p.Y);
    }

    /// <summary>所有显示器屏幕边界列表。</summary>
    public static List<Rect> AllScreenBounds()
    {
        var list = new List<Rect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
            {
                list.Add(ToRect(lprcMonitor));
                return true;
            }, IntPtr.Zero);
        return list;
    }

    /// <summary>点是否落在任一显示器屏幕内。</summary>
    public static bool IsPointOnAnyScreen(double x, double y)
    {
        foreach (var r in AllScreenBounds())
            if (r.Contains(new Point(x, y))) return true;
        return false;
    }

    /// <summary>
    /// 窗口是否与任一显示器有交集。用 MonitorFromWindow 由系统统一处理坐标系，
    /// 避免 WPF 的 DIP 坐标与物理像素直接比较导致的误判（多屏 + 不同缩放时）。
    /// </summary>
    public static bool IsWindowOnAnyScreen(IntPtr hwnd)
    {
        return MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
    }
}
