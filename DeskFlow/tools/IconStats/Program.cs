using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using DeskFlow.Core.Services;
using DeskFlow.Shell;

if (args.Length == 0)
{
    Console.WriteLine("用法: IconStats <max-items=60>");
    Console.WriteLine("扫描两个桌面目录的所有 .lnk / 文件，对每个条目调用 IconCache 提取 256px 图标，");
    Console.WriteLine("统计「非透明像素 bounding box / 总画布」比例 —— 找出 padding 大导致「看起来小」的图标。");
    return;
}

var maxItems = int.TryParse(args[0], out var n) ? n : 60;

var thread = new Thread(() =>
{
    var app = new System.Windows.Application();
    app.Startup += (_, _) =>
    {
        var dirs = DesktopScanner.GetDesktopDirectories();
        var paths = new List<string>();
        foreach (var d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(d))
                {
                    paths.Add(f);
                    if (paths.Count >= maxItems) break;
                }
            }
            catch { /* skip */ }
        }

        Console.WriteLine($"扫描 {paths.Count} 个条目");
        var cache = new IconCache();
        var pending = paths.Count;
        var done = 0;
        var padBig = new List<(string File, double Pct)>();
        var padSmall = new List<(string File, double Pct)>();

        foreach (var path in paths)
        {
            cache.GetAsync(path, 80, img =>
            {
                Interlocked.Increment(ref done);

                double pct;
                try
                {
                    if (img is BitmapSource bs)
                    {
                        pct = ComputeContentRatio(bs);
                    }
                    else
                    {
                        pct = -1;
                    }
                }
                catch
                {
                    pct = -1;
                }

                if (pct < 0)
                {
                    Console.WriteLine($"  ??  {Path.GetFileName(path)}: 不是 BitmapSource ({img.GetType().Name})");
                }
                else if (pct < 0.5)
                {
                    padBig.Add((Path.GetFileName(path), pct));
                }
                else
                {
                    padSmall.Add((Path.GetFileName(path), pct));
                }

                if (Interlocked.Decrement(ref pending) == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"=== 统计 ===");
                    Console.WriteLine($"总扫描: {paths.Count}");
                    Console.WriteLine($"内容占比 < 50% 的「看起来小」图标: {padBig.Count}");
                    Console.WriteLine($"内容占比 >= 50% 的正常图标:    {padSmall.Count}");
                    Console.WriteLine();
                    Console.WriteLine("=== 内容占比最低的 20 个（看起来最小的图标）===");
                    foreach (var (f, p) in padBig.OrderBy(x => x.Pct).Take(20))
                    {
                        Console.WriteLine($"  {p,6:P1}  {f}");
                    }

                    app.Dispatcher.BeginInvoke(() => app.Shutdown());
                }
            }, 1.0);
        }

        if (paths.Count == 0)
        {
            app.Shutdown();
            return;
        }

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Console.WriteLine($"超时：已完成 {done}/{paths.Count}");
            app.Shutdown();
        };
        timer.Start();
    };
    app.Run();
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

/// <summary>
/// 计算 BitmapSource 中「非透明像素 bounding box / 总像素」的面积比。
/// 值越小说明源图层透明 padding 越多（图标设计本身留白大）。
/// </summary>
static double ComputeContentRatio(BitmapSource bs)
{
    var w = bs.PixelWidth;
    var h = bs.PixelHeight;
    if (w <= 0 || h <= 0) return -1;

    var stride = w * 4;
    var pixels = new byte[h * stride];
    bs.CopyPixels(System.Windows.Int32Rect.Empty, pixels, stride, 0);

    int minX = w, minY = h, maxX = -1, maxY = -1;
    for (var y = 0; y < h; y++)
    {
        var row = y * stride;
        for (var x = 0; x < w; x++)
        {
            var a = pixels[row + x * 4 + 3];
            if (a >= 8) // 容忍极少量 alpha 噪点
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
    }

    if (maxX < 0) return 0; // 全透明
    var contentW = maxX - minX + 1;
    var contentH = maxY - minY + 1;
    return (double)(contentW * contentH) / (w * h);
}