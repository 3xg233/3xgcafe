using System;
using System.Drawing;

namespace WallpaperPure.Native;

internal static class TrayIconHelper
{
    /// <summary>
    /// 用 GDI+ 绘制一个 16×16 荧光黄圆点图标。
    /// </summary>
    public static IntPtr CreateIconHandle()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var brush = new SolidBrush(System.Drawing.Color.FromArgb(0xFA, 0xFC, 0x52));
            g.FillEllipse(brush, 1, 1, 14, 14);
        }
        return bmp.GetHicon();
    }
}
