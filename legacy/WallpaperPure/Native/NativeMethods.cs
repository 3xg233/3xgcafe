using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WallpaperPure.Native;

/// <summary>
/// 桌面图标显隐控制（v1.2 双通道方案）。
/// </summary>
/// <remarks>
/// - 立即生效：对桌面图标列表窗口（SysListView32）调用 ShowWindow，直接、无闪烁；
/// - 重启持久：写注册表 HKCU\...\Explorer\Advanced\HideIcons（与系统右键菜单
///   "查看 → 显示桌面图标"同一持久化位置），Explorer 重建桌面后保持状态；
/// - 状态读取以图标窗口的<b>实际可见性</b>为准（所见即所得），找不到窗口时回退注册表。
/// v1.1 曾用 SHGetSetSettings + SSF_HIDEICONS，因 SHELLSTATE 位域偏移与预期不符
/// （点击仅引起桌面闪烁、图标并未隐藏）已弃用。
/// </remarks>
internal static class NativeMethods
{
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private const string AdvancedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string HideIconsValueName = "HideIcons";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>显示/隐藏指定窗口（供过渡动画与最终状态应用使用）。</summary>
    public static void ShowWindow(IntPtr hWnd, bool show)
        => ShowWindow(hWnd, show ? SW_SHOW : SW_HIDE);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    // ---------- 分层窗口（切换过渡动画用） ----------

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>为窗口临时加上分层样式并设初始不透明度（动画开始）。</summary>
    public static void EnableLayered(IntPtr hWnd, byte alpha)
    {
        SetWindowLong(hWnd, GWL_EXSTYLE, GetWindowLong(hWnd, GWL_EXSTYLE) | WS_EX_LAYERED);
        SetLayeredWindowAttributes(hWnd, 0, alpha, LWA_ALPHA);
    }

    /// <summary>设置分层窗口不透明度（0~255）。</summary>
    public static void SetAlpha(IntPtr hWnd, byte alpha)
        => SetLayeredWindowAttributes(hWnd, 0, alpha, LWA_ALPHA);

    /// <summary>移除分层样式，窗口恢复 Explorer 原状（动画结束）。</summary>
    public static void DisableLayered(IntPtr hWnd)
        => SetWindowLong(hWnd, GWL_EXSTYLE, GetWindowLong(hWnd, GWL_EXSTYLE) & ~WS_EX_LAYERED);

    /// <summary>
    /// 定位桌面图标列表窗口（SysListView32）。
    /// 常规路径：Progman → SHELLDLL_DefView → SysListView32；
    /// 使用 Wallpaper Engine 等壁纸软件时，DefView 会被挂到某个 WorkerW 下，需遍历查找。
    /// </summary>
    public static IntPtr FindDesktopListView()
    {
        IntPtr defView = FindWindowExW(FindWindowW("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);

        if (defView == IntPtr.Zero)
        {
            EnumWindows((top, _) =>
            {
                IntPtr dv = FindWindowExW(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero)
                {
                    defView = dv;
                    return false; // 找到即停
                }
                return true;
            }, IntPtr.Zero);
        }

        return defView == IntPtr.Zero
            ? IntPtr.Zero
            : FindWindowExW(defView, IntPtr.Zero, "SysListView32", null);
    }

    /// <summary>桌面图标当前是否可见。优先看图标窗口实际可见性，找不到窗口时回退注册表。</summary>
    public static bool AreDesktopIconsVisible()
    {
        IntPtr listView = FindDesktopListView();
        if (listView != IntPtr.Zero)
            return IsWindowVisible(listView);
        return ReadHideIconsFlag() == 0;
    }

    /// <summary>设置桌面图标可见性：ShowWindow 立即生效，注册表 HideIcons 负责重启后持久。</summary>
    public static void SetDesktopIconsVisible(bool visible)
    {
        IntPtr listView = FindDesktopListView();
        if (listView != IntPtr.Zero)
            ShowWindow(listView, visible ? SW_SHOW : SW_HIDE);
        WriteHideIconsFlag(!visible);
    }

    private static int ReadHideIconsFlag()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(AdvancedKeyPath);
        return key?.GetValue(HideIconsValueName) as int? ?? 0;
    }

    private static void WriteHideIconsFlag(bool hidden)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(AdvancedKeyPath);
        key.SetValue(HideIconsValueName, hidden ? 1 : 0, RegistryValueKind.DWord);
    }
}
