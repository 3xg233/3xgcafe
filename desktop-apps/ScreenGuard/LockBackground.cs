using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenGuard.Native;

namespace ScreenGuard;

/// <summary>
/// 锁屏背景：抓取当前整屏画面 → 强模糊 → 压暗 → 轻微雾气，做成毛玻璃观感。
///
/// 为什么用"模糊快照"而不是实时模糊：
/// 1. 实时模糊只能靠 DWM 的 undocumented API（SetWindowCompositionAttribute），
///    在 Win10 / Win11 22H2+ 上表现不一致，还要求分层透明窗口（远程桌面下不可靠）；
/// 2. 静态快照不依赖任何系统版本特性，任何机器都能出效果；
/// 3. 快照经过 1/12 降采样后文字信息彻底丢失，且底层画面被不透明窗口完全盖住，
///    比"实时模糊 + 底层仍在渲染"的隐私性更好。
/// </summary>
internal static class LockBackground
{
    /// <summary>降采样倍数，越大越模糊。</summary>
    private const int DownscaleFactor = 12;

    /// <summary>压暗强度（0–255）。</summary>
    private const byte DimAlpha = 118;

    /// <summary>白色雾气强度，制造毛玻璃质感。</summary>
    private const byte HazeAlpha = 14;

    /// <summary>生成模糊压暗后的整屏快照；任何失败都返回 null，由调用方回退纯色背景。</summary>
    public static System.Windows.Media.ImageSource? CreateBlurred()
    {
        try
        {
            return Build();
        }
        catch
        {
            return null;
        }
    }

    private static System.Windows.Media.ImageSource? Build()
    {
        int originX = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int originY = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        if (width <= 0 || height <= 0)
            return null;

        // 1. 抓取整个虚拟桌面（含多显示器）
        using var captured = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(captured))
        {
            graphics.CopyFromScreen(originX, originY, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        // 2. 降采样到 1/12 —— 这一步已彻底抹掉文字信息
        int smallWidth = Math.Max(1, width / DownscaleFactor);
        int smallHeight = Math.Max(1, height / DownscaleFactor);

        using var small = new Bitmap(smallWidth, smallHeight, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(
                captured,
                new Rectangle(0, 0, smallWidth, smallHeight),
                new Rectangle(0, 0, width, height),
                GraphicsUnit.Pixel);
        }

        // 3. 小图上做盒式模糊，消除降采样的块状感
        BoxBlur(small, 2);

        // 4. 放大回原尺寸 + 压暗 + 雾气
        using var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(result))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(small, new Rectangle(0, 0, width, height));

            using (var dim = new SolidBrush(Color.FromArgb(DimAlpha, 0, 0, 0)))
                graphics.FillRectangle(dim, 0, 0, width, height);

            using (var haze = new SolidBrush(Color.FromArgb(HazeAlpha, 232, 238, 255)))
                graphics.FillRectangle(haze, 0, 0, width, height);
        }

        return ToImageSource(result);
    }

    /// <summary>把 Bitmap 转成 WPF 可用的 ImageSource（并修正 alpha 通道）。</summary>
    private static System.Windows.Media.ImageSource ToImageSource(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int stride;
        byte[] pixels;

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            stride = data.Stride;
            pixels = new byte[stride * height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        // GDI 抓屏不会写 alpha，字节全为 0；不修正的话 WPF 会渲染成全透明
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        var source = new System.Windows.Media.Imaging.WriteableBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        source.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, stride, 0);
        source.Freeze();
        return source;
    }

    /// <summary>可分离盒式模糊（小图上执行，开销可忽略）。</summary>
    private static void BoxBlur(Bitmap bitmap, int radius)
    {
        if (radius < 1)
            return;

        int width = bitmap.Width;
        int height = bitmap.Height;

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int length = stride * height;

            byte[] source = new byte[length];
            byte[] horizontal = new byte[length];
            byte[] vertical = new byte[length];
            Marshal.Copy(data.Scan0, source, 0, length);

            // 水平方向
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int b = 0, g = 0, r = 0, a = 0, count = 0;
                    int from = Math.Max(0, x - radius);
                    int to = Math.Min(width - 1, x + radius);

                    for (int k = from; k <= to; k++)
                    {
                        int index = row + k * 4;
                        b += source[index];
                        g += source[index + 1];
                        r += source[index + 2];
                        a += source[index + 3];
                        count++;
                    }

                    int target = row + x * 4;
                    horizontal[target] = (byte)(b / count);
                    horizontal[target + 1] = (byte)(g / count);
                    horizontal[target + 2] = (byte)(r / count);
                    horizontal[target + 3] = (byte)(a / count);
                }
            }

            // 垂直方向
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    int b = 0, g = 0, r = 0, a = 0, count = 0;
                    int from = Math.Max(0, y - radius);
                    int to = Math.Min(height - 1, y + radius);

                    for (int k = from; k <= to; k++)
                    {
                        int index = k * stride + x * 4;
                        b += horizontal[index];
                        g += horizontal[index + 1];
                        r += horizontal[index + 2];
                        a += horizontal[index + 3];
                        count++;
                    }

                    int target = y * stride + x * 4;
                    vertical[target] = (byte)(b / count);
                    vertical[target + 1] = (byte)(g / count);
                    vertical[target + 2] = (byte)(r / count);
                    vertical[target + 3] = (byte)(a / count);
                }
            }

            Marshal.Copy(vertical, 0, data.Scan0, length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
