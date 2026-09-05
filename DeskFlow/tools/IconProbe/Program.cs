using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskFlow.Shell;

// 端到端校验：真实走 IconCache（STA 提取线程 → 高质量降采样 → WPF 回调），
// 模拟 150% DPI，检查输出位图的物理像素与 DPI 是否符合"逻辑尺寸不变、不再被拉伸"。

if (args.Length == 0)
{
    Console.WriteLine("用法: IconProbe <文件路径...>");
    return;
}

const int logicalSize = 48;   // 设置里的图标边长（DIP）
const double dpiScale = 1.5;  // 模拟 150% 缩放

var thread = new Thread(() =>
{
    var app = new Application();
    app.Startup += (_, _) =>
    {
        var cache = new IconCache();
        var pending = args.Length;

        foreach (var raw in args)
        {
            var path = raw.Replace('/', '\\');
            cache.GetAsync(path, logicalSize, img =>
            {
                if (img is BitmapSource bs)
                {
                    var dip = bs.PixelWidth / (bs.DpiX / 96.0);
                    Console.WriteLine($"{Path.GetFileName(path)}: {bs.PixelWidth}x{bs.PixelHeight} " +
                                      $"DPI={bs.DpiX:0.#} → 显示 {dip:0.#} DIP（期望 {logicalSize}）");
                }
                else
                {
                    Console.WriteLine($"{Path.GetFileName(path)}: {img.GetType().Name}");
                }

                if (--pending == 0)
                {
                    app.Dispatcher.BeginInvoke(() => app.Shutdown());
                }
            }, dpiScale);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Console.WriteLine("超时：部分图标未返回");
            app.Shutdown();
        };
        timer.Start();
    };
    app.Run();
});

thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
