using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace DeskFlow.App;

/// <summary>运行时生成托盘图标，避免引入二进制资源文件。</summary>
public static class AppIcon
{
    private static Bitmap? _keepAlive;

    public static Icon Create()
    {
        const int size = 64;

        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.Clear(Color.Transparent);

            using var path = CreateRoundedRectangle(new Rectangle(3, 3, size - 6, size - 6), 14);
            using var background = new SolidBrush(Color.FromArgb(0x2B, 0x7A, 0xDE));
            graphics.FillPath(background, path);

            using var font = new Font("Segoe UI", 32, FontStyle.Bold, GraphicsUnit.Pixel);
            using var foreground = new SolidBrush(Color.White);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            graphics.DrawString("D", font, foreground, new RectangleF(0, 0, size, size), format);
        }

        // Icon.FromHandle 与位图共享句柄，需要保持位图存活
        _keepAlive = bitmap;
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }
}
