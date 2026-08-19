// ============================================================================
// IconRenderer.cs —— WPF 数字图标渲染与托盘 Icon 转换
// ============================================================================
// 数字使用 Windows 自带的 Segoe UI Semibold，按可用宽度自动缩放，并以
// cap-height 做光学居中；这样 16/24/32px 托盘图标中的数字更清晰、均衡。
// ============================================================================

using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace MetrikLite;

public static class IconRenderer
{
    private static readonly Typeface NumberTypeface = new(
        new FontFamily("Segoe UI"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    /// <summary>
    /// 渲染配额数字。图标只放数字本身，百分号保留在提示文字和详情面板中，
    /// 避免小尺寸图标中符号挤压数字。
    /// </summary>
    public static RenderTargetBitmap RenderPercent(int percent, Color color, bool lightGlyphs, int px)
    {
        px = Math.Clamp(px, 16, 64);
        percent = Math.Clamp(percent, 0, 100);
        var bitmap = NewBitmap(px);
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            var text = percent.ToString(CultureInfo.InvariantCulture);
            var room = Math.Max(1, px - 1.0);
            var em = px * 1.10;
            var glyphBounds = MeasureGlyphBounds(text, em);

            // 用实际字形边界而不是 FormattedText 的行盒高度测量。
            // 行盒包含额外的字体留白，会把 10 这类两位数缩得过小。
            var scale = Math.Min(
                room / Math.Max(1, glyphBounds.Width),
                room / Math.Max(1, glyphBounds.Height));
            em *= Math.Clamp(scale, 0.65, 1.35);
            glyphBounds = MeasureGlyphBounds(text, em);
            var formatted = BuildFormatted(text, em, Brushes.Black);
            var origin = new Point(
                px / 2 - glyphBounds.Left - glyphBounds.Width / 2,
                px / 2 - glyphBounds.Top - glyphBounds.Height / 2 + em * 0.045);
            var brush = new SolidColorBrush(color);
            brush.Freeze();

            if (lightGlyphs)
            {
                // 深色任务栏上用细黑色 halo 分隔白色数字与背景。
                var halo = new Pen(
                    new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12)),
                    Math.Max(0.9, px * 0.055));
                halo.Brush.Freeze();
                context.DrawGeometry(null, halo, BuildTextGeometry(text, em, origin));
            }

            context.DrawText(BuildFormatted(text, em, brush), origin);
        }

        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>把 WPF 位图转成 NotifyIcon 使用的 WinForms Icon。</summary>
    public static Drawing.Icon ToIcon(RenderTargetBitmap bitmap)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        using var drawingBitmap = new Drawing.Bitmap(
            width, height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = drawingBitmap.LockBits(
            new Drawing.Rectangle(0, 0, width, height),
            Drawing.Imaging.ImageLockMode.WriteOnly,
            Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            drawingBitmap.UnlockBits(data);
        }

        var handle = drawingBitmap.GetHicon();
        return Drawing.Icon.FromHandle(handle);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>释放托盘图标及其原生句柄。</summary>
    public static void SafeDispose(Drawing.Icon? icon)
    {
        if (icon == null)
        {
            return;
        }

        try
        {
            _ = DestroyIcon(icon.Handle);
        }
        catch
        {
            // 句柄已失效时仍继续释放托管对象。
        }
        finally
        {
            icon.Dispose();
        }
    }

    private static RenderTargetBitmap NewBitmap(int px)
        => new(px, px, 96, 96, PixelFormats.Pbgra32);

    private static FormattedText BuildFormatted(string text, double em, Brush brush)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            NumberTypeface, em, brush, 1.0);

    private static Rect MeasureGlyphBounds(string text, double em)
    {
        var formatted = BuildFormatted(text, em, Brushes.Black);
        return formatted.BuildGeometry(new Point(0, 0)).Bounds;
    }

    private static Geometry BuildTextGeometry(string text, double em, Point origin)
    {
        var formatted = BuildFormatted(text, em, Brushes.Black);
        return formatted.BuildGeometry(origin)
            .GetWidenedPathGeometry(new Pen(Brushes.Black, 0.01));
    }
}
