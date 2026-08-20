// ============================================================================
// IconRenderer.cs —— WPF 数字图标渲染与托盘 Icon 转换
// ============================================================================
// 数字使用 Windows 11 的 Segoe UI Variable Display Semibold，按实际字形边界
// 自动缩放，并保留上下安全区做光学居中；避免 8、10 等数字贴底或被裁切。
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
        new FontFamily("Segoe UI Variable Display"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    /// <summary>
    /// 渲染配额数字。图标只放数字本身，百分号保留在提示文字和详情面板中，
    /// 避免小尺寸图标中符号挤压数字。
    /// </summary>
    public static RenderTargetBitmap RenderPercent(int percent, Color color, bool lightGlyphs, int px)
        => RenderText(Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture), color, lightGlyphs, px);

    /// <summary>渲染状态字符，用于 Codex 尚未配置时仍保留可操作的托盘菜单。</summary>
    public static RenderTargetBitmap RenderStatus(string text, Color color, bool lightGlyphs, int px)
        => RenderText(text, color, lightGlyphs, px);

    private static RenderTargetBitmap RenderText(string text, Color color, bool lightGlyphs, int px)
    {
        px = Math.Clamp(px, 16, 64);
        var bitmap = NewBitmap(px);
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            // halo 和抗锯齿像素也需要空间。旧版把字形几乎撑满整个画布，
            // 再向下偏移 4.5% em，导致 8 的下缘在 16px 图标里看起来被压扁。
            var safety = Math.Max(1.25, px * 0.075);
            var room = Math.Max(1, px - safety * 2);
            var em = text.Length switch
            {
                1 => px * 1.18,
                2 => px * 1.12,
                _ => px * 1.02,
            };
            var glyphBounds = MeasureGlyphBounds(text, em);

            // 用实际字形边界而不是 FormattedText 的行盒高度测量。
            // 行盒包含额外的字体留白，会把 10 这类两位数缩得过小。
            var scale = Math.Min(
                room / Math.Max(1, glyphBounds.Width),
                room / Math.Max(1, glyphBounds.Height));
            em *= Math.Clamp(scale, 0.62, 1.32);
            glyphBounds = MeasureGlyphBounds(text, em);
            var origin = new Point(
                px / 2 - glyphBounds.Left - glyphBounds.Width / 2,
                px / 2 - glyphBounds.Top - glyphBounds.Height / 2 - em * 0.012);
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
