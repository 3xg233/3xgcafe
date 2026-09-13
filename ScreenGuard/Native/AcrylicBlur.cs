using System;
using System.Runtime.InteropServices;

namespace ScreenGuard.Native;

/// <summary>
/// DWM 亚克力实时模糊（SetWindowCompositionAttribute，Win10 1803+ / Win11 上稳定）。
/// 对窗口**后方**的内容实时高斯模糊 + 深色染色——后方程序画面变化会实时透出。
/// 未公开 API，但在 Win10/Win11 上被大量应用使用；失败时返回 false，由调用方回退。
/// </summary>
internal static class AcrylicBlur
{
    private const int ACCENT_DISABLED = 1;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    private const int WCA_ACCENT_POLICY = 19;

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    /// <summary>
    /// 开启实时模糊。tintArgb 为染色（0xAARRGGBB），建议深色 + 60~75% 不透明度。
    /// 返回 false 表示系统不支持（如远程桌面会话、旧系统），调用方应回退快照/纯色。
    /// </summary>
    public static bool Enable(IntPtr hwnd, uint tintArgb)
    {
        // API 要 ABGR 字节序
        uint abgr = (tintArgb & 0xFF000000u)
                  | ((tintArgb & 0x000000FFu) << 16)
                  | (tintArgb & 0x0000FF00u)
                  | ((tintArgb & 0x00FF0000u) >> 16);

        var policy = new ACCENT_POLICY
        {
            AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 2,
            GradientColor = abgr,
            AnimationId = 0
        };

        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>());
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = Marshal.SizeOf<ACCENT_POLICY>()
            };
            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>关闭实时模糊（窗口关闭时系统也会随之释放，此方法主要用于显式清理）。</summary>
    public static void Disable(IntPtr hwnd)
    {
        var policy = new ACCENT_POLICY { AccentState = ACCENT_DISABLED };
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>());
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = Marshal.SizeOf<ACCENT_POLICY>()
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch
        {
            // 忽略
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
