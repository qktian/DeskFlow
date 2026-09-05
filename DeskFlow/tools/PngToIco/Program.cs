using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length < 2)
{
    Console.WriteLine("用法: PngToIco <input.png> <output.ico> [roundedRadiusRatio] [whitenBackground]");
    Console.WriteLine("  roundedRadiusRatio: 圆角半径占短边的比例，默认 0（直角），常用 0.18~0.22");
    Console.WriteLine("  whitenBackground: 1 = 先做「白色背景透明化」，把接近白色的背景抠成透明");
    return;
}

var srcPath = args[0];
var outPath = args[1];
var radiusRatio = args.Length >= 3 && double.TryParse(args[2], out var rr) ? rr : 0.0;
var whiten = args.Length >= 4 && args[3] == "1";
var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };

using var src = new Bitmap(srcPath);

// 可选：白色背景透明化（在原大图上处理，避免缩小后边缘毛刺）
Bitmap? prepared = null;
if (whiten)
{
    prepared = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(prepared))
    {
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(src, 0, 0, src.Width, src.Height);
    }

    WhiteToTransparent(prepared);
    // 白色抠掉后四角已透明，但主体（深蓝方块）可能仍是直角 —— 用圆角蒙版裁主体四角
    if (radiusRatio > 0)
    {
        ApplyRoundedCorners(prepared, Math.Max(8, (int)Math.Round(prepared.Width * radiusRatio)));
    }
}

var images = new List<(int Size, byte[] Png)>();
foreach (var s in sizes)
{
    using var bmp = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.Clear(System.Drawing.Color.Transparent);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.DrawImage(prepared ?? (System.Drawing.Image)src, 0, 0, s, s);
    }

    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    images.Add((s, ms.ToArray()));
}

prepared?.Dispose();

using var fs = File.Create(outPath);
using var bw = new BinaryWriter(fs);
bw.Write((ushort)0);                 // reserved
bw.Write((ushort)1);                 // type: icon
bw.Write((ushort)images.Count);      // count

var offset = 6 + 16 * images.Count;
foreach (var (s, png) in images)
{
    bw.Write((byte)(s >= 256 ? 0 : s));
    bw.Write((byte)(s >= 256 ? 0 : s));
    bw.Write((byte)0);               // palette
    bw.Write((byte)0);               // reserved
    bw.Write((ushort)1);             // planes
    bw.Write((ushort)32);            // bitcount
    bw.Write((uint)png.Length);
    bw.Write((uint)offset);
    offset += png.Length;
}

foreach (var (_, png) in images)
{
    bw.Write(png);
}

Console.WriteLine($"已生成 {outPath}，包含 {images.Count} 个尺寸{(radiusRatio > 0 ? "（圆角）" : "")}");

static void ApplyRoundedCorners(Bitmap bmp, int radius)
{
    var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
    var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, bmp.PixelFormat);
    var stride = data.Stride;
    var w = bmp.Width;
    var h = bmp.Height;
    var buf = new byte[h * stride];
    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);

    // 四角裁剪：坐标为 (x,y) 的像素，若落入半径 r 的圆角区域外，则 alpha 置 0
    for (var y = 0; y < h; y++)
    {
        var row = y * stride;
        for (var x = 0; x < w; x++)
        {
            var inCorner = false;
            // 判断是否处于四个圆角矩形"外"
            if (x < radius && y < radius)
            {
                inCorner = IsOutsideRound(x, y, radius);
            }
            else if (x >= w - radius && y < radius)
            {
                inCorner = IsOutsideRound(w - 1 - x, y, radius);
            }
            else if (x < radius && y >= h - radius)
            {
                inCorner = IsOutsideRound(x, h - 1 - y, radius);
            }
            else if (x >= w - radius && y >= h - radius)
            {
                inCorner = IsOutsideRound(w - 1 - x, h - 1 - y, radius);
            }

            if (inCorner)
            {
                buf[row + x * 4 + 3] = 0;
                buf[row + x * 4 + 0] = 0;
                buf[row + x * 4 + 1] = 0;
                buf[row + x * 4 + 2] = 0;
            }
        }
    }

    System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, buf.Length);
    bmp.UnlockBits(data);
}

static bool IsOutsideRound(int dx, int dy, int r)
{
    // 距离圆心 (r-0.5, r-0.5) 大于 r 则在圆外
    var cx = r - 0.5;
    var cy = r - 0.5;
    var d = (dx - cx) * (dx - cx) + (dy - cy) * (dy - cy);
    return d > r * r;
}

/// <summary>
/// 把接近白色的背景像素淡出为透明（按「离白色距离」做 alpha 渐变，避免硬边）。
/// </summary>
static void WhiteToTransparent(Bitmap bmp)
{
    var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
    var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, bmp.PixelFormat);
    var stride = data.Stride;
    var w = bmp.Width;
    var h = bmp.Height;
    var buf = new byte[h * stride];
    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);

    for (var y = 0; y < h; y++)
    {
        var row = y * stride;
        for (var x = 0; x < w; x++)
        {
            var idx = row + x * 4;
            var r = buf[idx + 2];
            var g = buf[idx + 1];
            var b = buf[idx];
            var a = buf[idx + 3];
            if (a == 0) continue;

            // 到白色 (255,255,255) 的最大通道差：越小越接近白
            var m = Math.Max(Math.Abs(r - 255), Math.Max(Math.Abs(g - 255), Math.Abs(b - 255)));
            if (m < 30)
            {
                var na = Math.Min(255, m * 255 / 30);
                buf[idx + 3] = (byte)na;
                if (na < 4)
                {
                    buf[idx] = 0;
                    buf[idx + 1] = 0;
                    buf[idx + 2] = 0;
                }
            }
        }
    }

    System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, buf.Length);
    bmp.UnlockBits(data);
}