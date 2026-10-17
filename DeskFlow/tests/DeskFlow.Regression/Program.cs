using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskFlow.App.Services;
using DeskFlow.Core.Models;
using DeskFlow.Core.Services;
using DeskFlow.Shell;

// Dependency-free Windows regression runner. All file writes use a unique temp folder.
// Uses isolated fixture processes and temporary hotkey registrations; never alters desktop icons or user config.
internal static partial class Program
{
    private static int _failures;
    private static int _checks;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "DeskFlow.Regression", Guid.NewGuid().ToString("N"));

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--fixture-owner") { Thread.Sleep(Timeout.Infinite); return 0; }
        if (args.FirstOrDefault() == "--fixture-guardian")
        {
            DiagnosticLog.Current = new RollingLog(Path.Combine(args[1], "logs"));
            return DesktopRecovery.Watch(new RecoveryStore(args[1]), args[2],
                () => { File.WriteAllText(Path.Combine(args[1], "restored"), "yes"); return true; },
                () => File.WriteAllText(Path.Combine(args[1], "ready"), "yes"));
        }
        Directory.CreateDirectory(Root);
        DiagnosticLog.Current = new RollingLog(Path.Combine(Root, "logs"));
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            if (args.FirstOrDefault() == "--benchmark") { Benchmark(); return 0; }
            if (args.FirstOrDefault() == "--box-layout")
            {
                BoxLayout();
                Console.WriteLine($"{_checks - _failures}/{_checks} box-layout checks passed");
                return _failures == 0 ? 0 : 1;
            }
            if (args.FirstOrDefault() == "--todo-effect")
            {
                TodoEffect();
                Console.WriteLine($"{_checks - _failures}/{_checks} todo-effect checks passed");
                return _failures == 0 ? 0 : 1;
            }
            Icons();
            Rules();
            Files();
            Configuration();
            Todos();
            Layout();
            BoxLayout();
            Watcher();
            Reliability();
            if (args.Length > 0) RealIcon(args[0]);
        }
        catch (Exception ex) { _failures++; Console.Error.WriteLine(ex); }
        finally
        {
            // Root is the exact unique directory created by this process.
            Directory.Delete(Root, recursive: true);
        }
        Console.WriteLine($"{_checks - _failures}/{_checks} checks passed");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, Action action)
    {
        _checks++;
        try { action(); Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { _failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
    }

    private static void Require(bool condition, string message = "Unexpected result")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T IconMethod<T>(string method, params object[] args) =>
        (T)typeof(IconCache).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;

    private static BitmapSource Icon(int canvas, int width, int height, byte alpha = 255, byte color = 120)
    {
        var pixels = new byte[canvas * canvas * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = ((y + (canvas - height) / 2) * canvas + x + (canvas - width) / 2) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = (byte)(color * alpha / 255);
                pixels[offset + 3] = alpha;
            }
        var bitmap = BitmapSource.Create(canvas, canvas, 96, 96, PixelFormats.Pbgra32, null, pixels, canvas * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static (int Width, int Height) Bounds(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, image.PixelWidth * 4, 0);
        int left = image.PixelWidth, top = image.PixelHeight, right = -1, bottom = -1;
        for (var y = 0; y < image.PixelHeight; y++)
            for (var x = 0; x < image.PixelWidth; x++)
            {
                if (pixels[(y * image.PixelWidth + x) * 4 + 3] < 16) continue;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        return (Math.Max(0, right - left + 1), Math.Max(0, bottom - top + 1));
    }

    private static void Icons()
    {
        foreach (var dpi in new[] { 1.0, 1.5, 2.0 }) Check($"Padded icon at {dpi * 100}% DPI", () =>
        {
            var source = IconMethod<BitmapSource>("NormalizeIconCanvas", Icon(256, 48, 44));
            var result = IconMethod<BitmapSource>("Resample", source, (int)(48 * dpi), dpi);
            var bounds = Bounds(result);
            Require(bounds.Width >= 40 * dpi && bounds.Height >= 36 * dpi);
            Require(Math.Abs(result.Width - 48) < 0.1 && Math.Abs(result.Height - 48) < 0.1);
        });
        Check("Normal icon margins are unchanged", () =>
        {
            var source = Icon(256, 220, 220);
            Require(ReferenceEquals(source, IconMethod<BitmapSource>("NormalizeIconCanvas", source)));
        });
        Check("Empty icon is safe", () =>
        {
            var source = Icon(256, 0, 0);
            Require(ReferenceEquals(source, IconMethod<BitmapSource>("NormalizeIconCanvas", source)));
        });
        Check("Non-square content keeps aspect ratio", () =>
        {
            var source = IconMethod<BitmapSource>("NormalizeIconCanvas", Icon(256, 48, 24));
            var result = IconMethod<BitmapSource>("Resample", source, 96, 2.0);
            var bounds = Bounds(result);
            Require(bounds.Width / (double)bounds.Height is > 1.8 and < 2.2);
        });
        Check("Dark translucent pixels are not double-premultiplied", () =>
        {
            var result = IconMethod<BitmapSource>("Resample", Icon(48, 48, 48, 128, 64), 96, 2.0);
            var pixel = new byte[4];
            new FormatConvertedBitmap(result, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(48, 48, 1, 1), pixel, 4, 0);
            Require(Math.Abs(pixel[0] - 64) <= 3 && Math.Abs(pixel[3] - 128) <= 3, $"B={pixel[0]}, A={pixel[3]}");
        });
        Check("Ctrl+Alt+O does not require implicit Shift", () =>
        {
            var upper = HotkeyParser.Parse("Ctrl+Alt+O")!;
            Require(upper == HotkeyParser.Parse("Ctrl+Alt+o"));
            Require(upper.Modifiers == 3 && upper.VirtualKey == 0x4F);
        });
    }

    private static void Rules()
    {
        Check("User PDF rule overrides default document rule", () =>
        {
            var config = ConfigService.CreateDefault();
            var rule = new RuleModel { Name = "PDF", Extensions = new() { "pdf" }, TargetBoxId = config.Boxes[0].Id };
            RuleEngine.AddOverride(config.Rules, rule);
            var file = new FileItem { Extension = "pdf", Kind = FileItemKind.Document };
            Require(ReferenceEquals(RuleEngine.Match(file, config.Rules), rule));
            rule.Enabled = false;
            Require(RuleEngine.Match(file, config.Rules)?.Name == "文档");
        });
        Check("Unmatched files go to unclassified", () =>
        {
            var config = ConfigService.CreateDefault();
            var result = RuleEngine.Classify(new[] { new FileItem { Path = "sample.unknown" } }, config.Rules, config.Boxes);
            Require(result[config.UnclassifiedBox!.Id].Single() == "sample.unknown");
        });
        Check("Batch classification retains all 10000 items", () =>
        {
            var config = ConfigService.CreateDefault();
            var files = Enumerable.Range(0, 10000).Select(i => new FileItem { Path = $"{i}.pdf", Kind = FileItemKind.Document });
            var watch = Stopwatch.StartNew();
            var result = RuleEngine.Classify(files, config.Rules, config.Boxes);
            Require(result.Sum(kv => kv.Value.Count) == 10000);
            Console.WriteLine($"  10000 items / 8 rules: {watch.Elapsed.TotalMilliseconds:F1} ms (classification only)");
        });
    }

    private static void Files()
    {
        foreach (var invalid in new[] { "../escape", @"..\escape", @"C:\escape", "x:y", "CON.txt", "LPT1", "trailing.", "name " })
            Check($"Reject rename target {invalid}", () =>
            {
                try { FileNamePolicy.RenameTarget(Path.Combine(Root, "old.txt"), invalid, false); }
                catch (ArgumentException) { return; }
                throw new InvalidOperationException("Invalid rename accepted");
            });
        Check("Rename retains extension and source directory", () =>
            Require(FileNamePolicy.RenameTarget(Path.Combine(Root, "old.txt"), "new", false) == Path.Combine(Root, "new.txt")));
        Check("Successful rename preserves content", () =>
        {
            var source = Path.Combine(Root, "old.txt");
            File.WriteAllText(source, "preserve me");
            var target = FileOperationService.Rename(source, "new");
            Require(target == Path.Combine(Root, "new.txt") && File.ReadAllText(target) == "preserve me" && !File.Exists(source));
        });
        Check("Rename retains manual and excluded assignments, including child paths", () =>
        {
            var source = Path.Combine(Root, "before");
            var target = Path.Combine(Root, "after");
            var other = Path.Combine(Root, "before-other.txt");
            var config = new AppConfig { Boxes = new() { new BoxModel { Items = new() { source, Path.Combine(source, "child.txt"), other }, ManualItems = new() { source } } }, ExcludedPaths = new() { Path.Combine(source, "excluded.txt") } };
            PathAssignments.Rename(config, source, target);
            Require(config.Boxes[0].Items.SequenceEqual(new[] { target, Path.Combine(target, "child.txt"), other }));
            Require(config.Boxes[0].ManualItems.Single() == target && config.ExcludedPaths.Single() == Path.Combine(target, "excluded.txt"));
        });
        Check("Bulk drop performs one save and preserves manual assignments", () =>
        {
            var config = ConfigService.CreateDefault();
            var saves = 0;
            var manager = new BoxManager(config, _ => { saves++; return true; });
            using var icons = manager.Icons;
            var files = Enumerable.Range(0, 10).Select(i => Path.Combine(Root, $"bulk{i}.txt")).ToArray();
            foreach (var path in files) File.WriteAllText(path, "test");
            manager.AddItemsToBox(config.Boxes[0].Id, files, true);
            Require(saves == 1 && config.Boxes[0].Items.Count == 10 && config.Boxes[0].ManualItems.Count == 10);
        });
        Check("Incremental refresh handles create, delete, exclusion and external rename", () =>
        {
            var config = ConfigService.CreateDefault();
            var manager = new BoxManager(config, _ => true);
            using var icons = manager.Icons;
            var old = Path.Combine(Root, "tracked.pdf");
            var renamed = Path.Combine(Root, "renamed.pdf");
            var fresh = Path.Combine(Root, "fresh.pdf");
            var excluded = Path.Combine(Root, "excluded.pdf");
            foreach (var path in new[] { old, fresh, excluded }) File.WriteAllText(path, "test");
            manager.AddItemToBox(config.Boxes[0].Id, old, true);
            config.ExcludedPaths.Add(excluded);
            File.Move(old, renamed);
            manager.RenameItem(old, renamed);
            manager.UpdateChangedItems(new[] { old, renamed, fresh, excluded });
            Require(config.Boxes[0].Items.Contains(renamed) && config.Boxes[0].ManualItems.Contains(renamed));
            Require(config.Boxes.Single(b => b.Name == "文档").Items.Contains(fresh));
            Require(!config.Boxes.SelectMany(b => b.Items).Contains(excluded));
            File.Delete(fresh);
            manager.UpdateChangedItems(new[] { fresh });
            Require(!config.Boxes.SelectMany(b => b.Items).Contains(fresh));
        });
    }

    private static void Configuration()
    {
        Check("Atomic save, backup recovery and corrupt-primary protection", () =>
        {
            var directory = Path.Combine(Root, "config");
            var config = ConfigService.CreateDefault();
            config.Boxes[0].Name = "first";
            Require(ConfigService.Save(config, directory));
            config.Boxes[0].Name = "second";
            Require(ConfigService.Save(config, directory));
            File.WriteAllText(Path.Combine(directory, "config.json"), "{broken");
            var recovered = ConfigService.Load(directory);
            Require(recovered.Boxes[0].Name == "first");
            Require(ConfigService.Save(recovered, directory));
            File.WriteAllText(Path.Combine(directory, "config.json"), "{broken again");
            Require(ConfigService.Load(directory).Boxes[0].Name == "first");
            Require(!Directory.EnumerateFiles(directory, "*.tmp").Any());
        });
        Check("Unchanged saves do not rewrite primary or backup", () =>
        {
            var directory = Path.Combine(Root, "no-op-config");
            var config = ConfigService.CreateDefault();
            Require(ConfigService.Save(config, directory));
            var path = Path.Combine(directory, "config.json");
            var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, timestamp);
            Require(ConfigService.Save(config, directory));
            Require(File.GetLastWriteTimeUtc(path) == timestamp && !File.Exists(Path.Combine(directory, "config.bak.json")));
        });
        Check("Save failures are reported and return false", () =>
        {
            var blocker = Path.Combine(Root, "not-a-directory");
            File.WriteAllText(blocker, "block");
            Exception? error = null;
            void Handler(Exception e) => error = e;
            ConfigService.SaveFailed += Handler;
            try { Require(!ConfigService.Save(ConfigService.CreateDefault(), blocker) && error is not null); }
            finally { ConfigService.SaveFailed -= Handler; }
        });
        Check("Malformed optional collections recover without losing valid boxes", () =>
        {
            var directory = Path.Combine(Root, "null-config");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "config.json"), """
                {"Boxes":[null,{"Id":"kept","Name":"Kept","Items":[null,"a"]}],
                 "Rules":[null,{"TargetBoxId":"kept","Extensions":null,"NameKeywords":null}],
                 "TodoBoards":[null,{"Items":[null]}],"Snapshots":[null,{"Boxes":[null,{}],"ExcludedPaths":null}]}
                """);
            var config = ConfigService.Load(directory);
            Require(config.Boxes.Any(b => b.Id == "kept") && config.Rules.Single().Extensions.Count == 0);
            Require(config.TodoBoards.Single().Items.Count == 0 && config.Snapshots.Single().Boxes.Count == 1);
        });
        Check("Layout snapshot restores box and todo geometry, style and assignments", () =>
        {
            var file = Path.Combine(Root, "snapshot-file.txt");
            File.WriteAllText(file, "snapshot");
            var box = new BoxModel { Name = "Snapshot", X = 20, Y = 20, Width = 320, Height = 260,
                Items = new() { file }, ManualItems = new() { file }, Style = new BoxStyle { BackgroundColor = "#AA112233" } };
            var board = new TodoBoardModel { X = 30, Y = 30, Width = 300, Height = 280, AutoHeight = false,
                Style = new BoxStyle { BackgroundColor = "#BB445566" } };
            var config = new AppConfig { Boxes = new() { box }, TodoBoards = new() { board } };
            var manager = new BoxManager(config, _ => true);
            try
            {
                var snapshot = manager.CreateSnapshot("layout");
                box.X = 100; box.Items.Clear(); box.ManualItems.Clear(); box.Style.BackgroundColor = "#00000000";
                board.Y = 100; board.Style.BackgroundColor = "#00000000";
                var result = manager.RestoreSnapshot(snapshot);
                Require(result.Restored == 1 && box.X == 20 && box.Style.BackgroundColor == "#AA112233");
                Require(board.Y == 30 && board.Style.BackgroundColor == "#BB445566");
            }
            finally { manager.Icons.Dispose(); }
        });
        Check("Legacy assignment snapshot leaves current layout untouched", () =>
        {
            var box = new BoxModel { X = 110, Y = 80 };
            var config = new AppConfig { Boxes = new() { box } };
            var manager = new BoxManager(config, _ => true);
            try
            {
                manager.RestoreSnapshot(new BoxSnapshot { Boxes = new() { new BoxItemsSnapshot { BoxId = box.Id } } });
                Require(box.X == 110 && box.Y == 80);
            }
            finally { manager.Icons.Dispose(); }
        });
    }

    private static void Todos()
    {
        Check("Monthly cycles clamp to February and return to March 31", () =>
        {
            var start = new DateTime(2024, 1, 31);
            Require(TodoRecurrence.CurrentCycle(start, RecurrencePeriod.Monthly, new DateTime(2024, 3, 1)) == new DateTime(2024, 2, 29));
            Require(TodoRecurrence.NextCycle(start, RecurrencePeriod.Monthly, new DateTime(2024, 2, 29)) == new DateTime(2024, 3, 31));
        });
        Check("Recurring completion resets next day", () =>
        {
            var task = new TodoItemModel { Kind = TodoKind.Recurring, StartDate = new DateTime(2026, 1, 1) };
            task.CheckIns["2026-01-01"] = true;
            Require(TodoRecurrence.IsCompletedForCycle(task, new DateTime(2026, 1, 1)));
            Require(!TodoRecurrence.IsCompletedForCycle(task, new DateTime(2026, 1, 2)));
        });
        Check("Malformed todo parent cycles are broken", () =>
        {
            var board = new TodoBoardModel { Items = new() { new() { Id = "a", ParentId = "b" }, new() { Id = "b", ParentId = "a" }, new() { Id = "c", ParentId = "c" } } };
            board.Normalize();
            foreach (var task in board.Items)
            {
                var seen = new HashSet<string>();
                var current = task;
                while (current is not null)
                {
                    Require(seen.Add(current.Id));
                    current = board.Items.FirstOrDefault(t => t.Id == current.ParentId);
                }
            }
        });
    }

    private static void Layout()
    {
        var area = new Rect(0, 0, 1920, 1040);
        var peer = new Rect(1528, 4, 388, 707);
        Check("Screenshot: periodic auto height keeps todo top aligned at 4", () =>
        {
            var rect = new Rect(1227, 4, 296, 76);
            foreach (var height in new[] { 76d, 200d, 950d, 76d })
            {
                rect = PanelLayout.AutoHeight(rect, height, 76, area);
                Require(rect.Top == peer.Top && rect.Left == 1227);
            }
        });
        Check("Screen-edge alignment survives repeated fit and auto-height cycles", () =>
        {
            var rect = new Rect(0, 0, 296, 76);
            for (var i = 0; i < 100; i++)
            {
                rect = PanelLayout.FitToArea(rect, area);
                rect = PanelLayout.AutoHeight(rect, 400, 76, area);
                Require(rect.Left == 0 && rect.Top == 0);
            }
        });
        Check("Auto-height overflow keeps top and limits height", () =>
        {
            var result = PanelLayout.AutoHeight(new Rect(400, 800, 296, 76), 600, 76, area);
            Require(result.Top == 800 && result.Height == 240 && result.Bottom == area.Bottom);
        });
        Check("Already visible geometry is not changed by screen recovery", () =>
        {
            foreach (var rect in new[] { peer, new Rect(0, 0, 300, 300), new Rect(1620, 740, 300, 300) })
                Require(PanelLayout.FitToArea(rect, area) == rect);
        });
        Check("Disconnected-screen panels are recovered and fitting is idempotent", () =>
        {
            var result = PanelLayout.FitToArea(new Rect(3000, -400, 500, 400), area);
            Require(area.Contains(result) && PanelLayout.FitToArea(result, area) == result);
        });
        Check("Collapsed panel is clamped using its visible title height", () =>
        {
            var rect = new Rect(100, 1008, 300, 32);
            Require(PanelLayout.FitToArea(rect, area) == rect);
        });
        Check("Negative-coordinate screens preserve edge-aligned panels", () =>
        {
            var secondary = new Rect(-1920, -200, 1920, 1080);
            var rect = new Rect(-1920, -200, 300, 300);
            Require(PanelLayout.FitToArea(rect, secondary) == rect);
        });
        Check("Spanning panel chooses greatest monitor overlap instead of enumeration order", () =>
        {
            var monitors = new[] { new MonitorArea("left", new Rect(-1920, 0, 1920, 1040), new Rect(-1920, 0, 1920, 1080), false),
                new MonitorArea("primary", area, area, true) };
            Require(ScreenHelper.SelectMonitor(new Rect(-10, 100, 300, 300), monitors, "left").Id == "primary");
            Require(ScreenHelper.SelectMonitor(new Rect(4000, 100, 300, 300), monitors, "missing").Id == "primary");
        });
        Check("File and todo panels use the same top alignment candidates", () =>
        {
            var snap = new PanelSnapSession();
            var rect = snap.Move(new Rect(1227, 12, 296, 76), new[] { peer }, area, true, false);
            Require(rect.Top == 4);
        });
        Check("Snap retains alignment during jitter but releases on absolute displacement", () =>
        {
            var snap = new PanelSnapSession();
            var raw = new Rect(1227, 12, 296, 76);
            Require(snap.Move(raw, new[] { peer }, area, true, false).Top == 4);
            raw.Y = 20;
            Require(snap.Move(raw, new[] { peer }, area, true, false).Top == 4);
            raw.Y = 30;
            Require(snap.Move(raw, new[] { peer }, area, true, false).Top == 30);
        });
        Check("Disabled snapping releases a previously held alignment", () =>
        {
            var snap = new PanelSnapSession();
            var raw = new Rect(1227, 12, 296, 76);
            snap.Move(raw, new[] { peer }, area, true, true);
            Require(snap.Move(raw, new[] { peer }, area, false, false) == raw);
        });
        Check("Screen and peer snapping select the nearest line without a release-time jump", () =>
        {
            var snap = new PanelSnapSession();
            Require(snap.Move(new Rect(1200, 5, 296, 76), new[] { peer }, area, true, true).Top == 4);
            snap.Reset();
            Require(snap.Move(new Rect(1200, 1, 296, 76), new[] { peer }, area, true, true).Top == 0);
        });
        Check("Horizontal resizing never changes height or top, even near peer edges", () =>
        {
            var start = new Rect(100, 4, 296, 76);
            var min = new Size(220, 76);
            var max = new Size(700, 1000);
            var raw = PanelLayout.ResizeFromStart(start, new Vector(50, 80), ResizeEdges.Right, min, max);
            var snapped = new PanelSnapSession().Resize(raw, ResizeEdges.Right, new[] { new Rect(450, 7, 300, 80) }, area, true, true, min, max);
            Require(snapped.Top == 4 && snapped.Height == 76 && snapped.Right == 450);
        });
        Check("Vertical resizing never changes width or left", () =>
        {
            var start = new Rect(103, 20, 296, 300);
            var min = new Size(160, 120);
            var max = new Size(1200, 1000);
            var raw = PanelLayout.ResizeFromStart(start, new Vector(200, 30), ResizeEdges.Bottom, min, max);
            var snapped = new PanelSnapSession().Resize(raw, ResizeEdges.Bottom, new[] { new Rect(100, 350, 300, 300) }, area, true, true, min, max);
            Require(snapped.Left == 103 && snapped.Width == 296 && snapped.Bottom == 350);
        });
        Check("Top-left resize keeps opposite edges and obeys minimum size", () =>
        {
            var start = new Rect(100, 100, 300, 300);
            var result = PanelLayout.ResizeFromStart(start, new Vector(250, 250), ResizeEdges.Left | ResizeEdges.Top, new Size(160, 120), new Size(1200, 1000));
            Require(result.Right == start.Right && result.Bottom == start.Bottom && result.Width == 160 && result.Height == 120);
        });
        Check("Disabling panel snap also disables resize snapping", () =>
        {
            var raw = new Rect(100, 100, 298, 300);
            var result = new PanelSnapSession().Resize(raw, ResizeEdges.Right, new[] { new Rect(400, 100, 300, 300) }, area, false, false, new Size(160, 120), new Size(1200, 1000));
            Require(result == raw);
        });
        Check("Fractional layout coordinates survive save and reload", () =>
        {
            var config = ConfigService.CreateDefault();
            config.Boxes[0].X = 150.6666666667;
            config.Boxes[0].Y = 4.6666666667;
            config.TodoBoards.Add(new TodoBoardModel { X = 450, Y = config.Boxes[0].Y });
            var directory = Path.Combine(Root, "layout-config");
            Require(ConfigService.Save(config, directory));
            var loaded = ConfigService.Load(directory);
            Require(loaded.Boxes[0].Y == loaded.TodoBoards[0].Y && loaded.Boxes[0].X == config.Boxes[0].X);
        });
    }

    private static void Watcher()
    {
        Check("Watcher batches events and preserves rename identity", () =>
        {
            var directory = Path.Combine(Root, "watch");
            Directory.CreateDirectory(directory);
            var oldPath = Path.Combine(directory, "old.txt");
            var newPath = Path.Combine(directory, "new.txt");
            File.WriteAllText(oldPath, "old");
            using var watcher = new DesktopWatcher();
            var events = new List<DesktopChangedEventArgs>();
            watcher.DesktopChanged += (_, e) => events.Add(e);
            watcher.Start(new[] { directory });
            File.Move(oldPath, newPath);
            for (var i = 0; i < 20; i++) File.AppendAllText(newPath, "x");
            PumpUntil(() => events.Any(e => e.Renames.Any(r => r.OldPath == oldPath && r.NewPath == newPath)));
            Require(events.SelectMany(e => e.Paths).Contains(newPath));
            Require(events.Count < 20);
        });
    }

    private static void PumpUntil(Func<bool> done)
    {
        var frame = new DispatcherFrame();
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (done() || watch.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Require(done(), "Timed out");
    }

    private static void RealIcon(string path)
    {
        Check("Real shortcut fills icon canvas at 100/150/200% DPI", () =>
        {
            foreach (var dpi in new[] { 1.0, 1.5, 2.0 })
            {
                var result = IconMethod<BitmapSource>("Extract", path, (int)(48 * dpi), dpi);
                var bounds = Bounds(result);
                Console.WriteLine($"  {Path.GetFileName(path)} at {dpi * 100}%: {bounds.Width}x{bounds.Height} content / {result.PixelWidth}px canvas");
                Require(bounds.Width > result.PixelWidth * 0.8 && Math.Abs(result.Width - 48) < 0.1);
            }
        });
        Check("Async cache distinguishes identical physical sizes at different DPI", () =>
        {
            using var cache = new IconCache();
            BitmapSource? first = null, second = null;
            cache.GetAsync(path, 48, image => first = (BitmapSource)image, 2.0);
            PumpUntil(() => first is not null);
            cache.GetAsync(path, 96, image => second = (BitmapSource)image, 1.0);
            PumpUntil(() => second is not null);
            Require(Math.Abs(first!.Width - 48) < 0.1 && Math.Abs(second!.Width - 96) < 0.1);
        });
        Check("Cache invalidation accepts a fresh request and disposal is idempotent", () =>
        {
            using var cache = new IconCache();
            cache.GetAsync(path, 48, _ => { }, 1.0);
            cache.Remove(path);
            BitmapSource? fresh = null;
            cache.GetAsync(path, 48, image => fresh = (BitmapSource)image, 1.0);
            PumpUntil(() => fresh is not null);
            Require(Bounds(fresh!).Width > 40);
            cache.Dispose();
            cache.Dispose();
            cache.GetAsync(path, 48, _ => throw new InvalidOperationException("Callback after disposal"));
        });
    }
}
