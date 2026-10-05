using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeskFlow.App;
using DeskFlow.App.Services;
using DeskFlow.Core.Models;
using DeskFlow.Core.Services;

internal static partial class Program
{
    private static void Benchmark()
    {
        Console.WriteLine("Count,ColdScanMs,WarmScanMs,MetadataHitRate,FirstFrameMs,RealizedRows,ManagedMB,UiStallsOver50ms");
        foreach (var count in new[] { 100, 1000, 5000 })
        {
            var dir = Path.Combine(Root, "benchmark-" + count);
            Directory.CreateDirectory(dir);
            for (var i = 0; i < count; i++) File.WriteAllBytes(Path.Combine(dir, $"item-{i:D5}.txt"), Array.Empty<byte>());
            FileMetadataCache.Clear();
            var start = Stopwatch.StartNew();
            var files = DesktopScanner.ScanDirectory(dir);
            var cold = start.Elapsed.TotalMilliseconds;
            start.Restart(); DesktopScanner.ScanDirectory(dir);
            var warm = start.Elapsed.TotalMilliseconds;
            var stats = FileMetadataCache.Statistics;
            var box = new BoxModel { Name = "Benchmark", Width = 320, Height = 400, Items = files.Select(f => f.Path).ToList() };
            var config = new AppConfig { Boxes = new List<BoxModel> { box } };
            var manager = new BoxManager(config, _ => true);
            var window = new BoxWindow(box, manager) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, IsHitTestVisible = false };
            var stalls = 0;
            var lastTick = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) => { if (lastTick.ElapsedMilliseconds > 50) stalls++; lastTick.Restart(); };
            timer.Start();
            start.Restart(); window.Show();
            var firstFrame = 0.0;
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            { firstFrame = start.Elapsed.TotalMilliseconds; frame.Continue = false; }));
            Dispatcher.PushFrame(frame);
            var settle = new DispatcherFrame();
            var settleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            settleTimer.Tick += (_, _) => { settleTimer.Stop(); settle.Continue = false; };
            settleTimer.Start(); Dispatcher.PushFrame(settle);
            timer.Stop();
            var list = (ListBox)window.FindName("ItemsScroll");
            var realized = 0;
            for (var i = 0; i < list.Items.Count; i++)
                if (list.ItemContainerGenerator.ContainerFromIndex(i) is not null) realized++;
            var memory = GC.GetTotalMemory(true) / 1024.0 / 1024.0;
            Console.WriteLine($"{count},{cold:F1},{warm:F1},{stats.Hits * 100.0 / Math.Max(1, stats.Hits + stats.Misses):F1}%,{firstFrame:F1},{realized},{memory:F1},{stalls}");
            window.Close(); manager.Icons.Dispose();
        }
    }
}
