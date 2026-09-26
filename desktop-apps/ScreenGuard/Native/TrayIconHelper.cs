using System;
using System.Drawing;

namespace ScreenGuard.Native;

internal static class TrayIconHelper
{
    /// <summary>用 GDI+ 画一个 16×16 的锁形图标（荧光黄）。</summary>
    public static IntPtr CreateIconHandle()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            using var pen = new Pen(System.Drawing.Color.FromArgb(0xFA, 0xFC, 0x52), 1.8f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            };
            using var brush = new SolidBrush(System.Drawing.Color.FromArgb(0xFA, 0xFC, 0x52));

            // 锁梁
            g.DrawArc(pen, 4.5f, 2.5f, 7f, 8f, 180, 180);
            // 锁体
            g.FillRectangle(brush, 3f, 7.5f, 10f, 6.5f);
        }
        return bmp.GetHicon();
    }
}
