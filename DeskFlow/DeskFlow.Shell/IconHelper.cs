using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskFlow.Shell;

/// <summary>
/// 图标提取 + 缓存 + 后台分发。
/// - 统一以 256px 请求源图，并带 <c>SIIGBF_BIGGERSIZEOK</c> 让 Shell 返回原生高清图层
///   （低清源图不会被 Shell 强行放大成糊图），再逐级对半降采样到"显示尺寸 × DPI 缩放"。
/// - .lnk 首选 IShellItemImageFactory 直接取 .lnk 自身图标：可解析快捷方式自定义图标
///   位置与索引、返回原生高清图层且不含箭头遮罩；失败再降级到系统图像列表。
/// - 后台 STA 线程提取，主线程零阻塞；提取失败自动按占位图回调，不丢条目。
/// - 缓存按 目标像素尺寸|path 索引，命中走缓存、未命中入队、提取完成后回调主线程。
/// </summary>
public sealed class IconCache : IDisposable
{
    /// <summary>
    /// 向 Shell 请求的源尺寸。256 是标准尺寸上限，.ico 中最精细的图层（通常是 PNG 压缩的那层）。
    /// 取到后再降采样到实际显示尺寸，降采样永远比"小图放大"清晰。
    /// IShellItemImageFactory 默认只缩小不放大，因此源图不足时不会被拉伸成糊图。
    /// </summary>
    private const int SourceSize = 256;

    /// <summary>Bitmap payload budget. WPF images also have overhead, so leave headroom.</summary>
    private const long MaxCachedBytes = 64L * 1024 * 1024;
    private const int MaxCachedEntries = 5_000;

    /// <summary>
    /// Windows 标准图标尺寸。.ico 文件只包含这些尺寸的原生图层
    /// （非标准尺寸如 52 没有对应图层，只能从邻近尺寸缩放，必然模糊）。
    /// </summary>
    private static readonly int[] StandardIconSizes = { 16, 32, 48, 64, 96, 128, 256 };

    /// <summary>
    /// 把任意尺寸对齐到最近的标准尺寸。
    /// 关键作用：让 Shell 提取时能返回该尺寸的原生图层、1:1 像素完美渲染，
    /// 避免非整数倍缩放导致的像素错位、糊、锯齿。
    /// </summary>
    public static int SnapToStandardSize(int size)
    {
        return StandardIconSizes.OrderBy(s => Math.Abs(s - size)).First();
    }

    private static Guid ItemImageFactoryGuid =
        new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    private readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastAccess = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _entryBytes = new(StringComparer.OrdinalIgnoreCase);
    private long _cachedBytes;
    private long _hits;
    private long _misses;
    private long _evictions;

    public (long Bytes, long Hits, long Misses, long Evictions) Statistics
    {
        get { lock (_sync) return (_cachedBytes, _hits, _misses, _evictions); }
    }
    private readonly Dictionary<string, List<Action<ImageSource>>> _waiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    private readonly BlockingCollection<IconRequest> _queue = new();
    private readonly Thread _worker;
    private bool _disposed;

    // 系统图像列表是 STA 对象，按线程缓存（提取线程固定且唯一），避免跨套间调用失败
    [ThreadStatic] private static IImageList? _jumboImageList;
    [ThreadStatic] private static IImageList? _extraLargeImageList;

    public IconCache()
    {
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "DeskFlow.IconExtractor"
        };
        // 系统图像列表（IImageList）是 STA 对象，跨套间调用会失败，提取线程必须是 STA。
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    /// <summary>
    /// 清空全部缓存。DPI 或图标尺寸变化后，旧位图的像素尺寸已不匹配，需要重新提取。
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _cache.Clear();
            _lastAccess.Clear();
            _entryBytes.Clear();
            _cachedBytes = 0;
            _waiters.Clear();
        }
    }

    /// <summary>
    /// 同步查缓存。不在缓存返回 false，避免阻塞 UI 线程做实际提取。
    /// 主要用于首次绑定模型时给一个占位、然后再触发异步加载。
    /// </summary>
    public bool TryGet(string path, int size, out ImageSource? image, double dpiScale = 0)
    {
        image = null;
        if (string.IsNullOrEmpty(path)) return false;
        var key = MakeKey(path, size, dpiScale, out _, out _);
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                _lastAccess[key] = DateTime.UtcNow.Ticks;
                _hits++;
                image = existing;
                return true;
            }
            _misses++;
        }
        return false;
    }

    /// <summary>
    /// 异步拿图：命中缓存直接回调；未命中入队后台提取，提取完成后回调。
    /// 回调在 UI 线程上执行（通过 System.Windows.Application.Current.Dispatcher）。
    /// </summary>
    public void GetAsync(string path, int size, Action<ImageSource> callback, double dpiScale = 0)
    {
        Request(path, size, callback, dpiScale);
    }

    /// <summary>
    /// 移除指定 path 的所有 size 缓存。文件被替换后调用，触发下次重新提取。
    /// </summary>
    public void Remove(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        lock (_sync)
        {
            var keys = _cache.Keys.Where(k => k.EndsWith("|" + path, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var k in keys)
            {
                _cache.Remove(k);
                _lastAccess.Remove(k);
                if (_entryBytes.Remove(k, out var bytes)) _cachedBytes -= bytes;
            }
            foreach (var k in _waiters.Keys.Where(k => k.EndsWith("|" + path, StringComparison.OrdinalIgnoreCase)).ToList())
                _waiters.Remove(k);
        }
    }

    /// <summary>
    /// 请求图标。命中缓存直接回调；未命中入队后台提取。
    /// 回调在主线程上执行（通过 Application.Current.Dispatcher）。
    /// </summary>
    public void Request(string path, int size, Action<ImageSource> callback, double dpiScale = 0)
    {
        if (_disposed) return;
        if (string.IsNullOrEmpty(path)) return;

        var key = MakeKey(path, size, dpiScale, out var pixelSize, out var scale);

        lock (_sync)
        {
            if (_disposed) return;
            if (_cache.TryGetValue(key, out var existing))
            {
                _lastAccess[key] = DateTime.UtcNow.Ticks;
                _hits++;
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(callback, existing);
                return;
            }
            _misses++;

            if (_waiters.TryGetValue(key, out var list))
            {
                list.Add(callback);
                return;
            }
            var callbacks = new List<Action<ImageSource>> { callback };
            _waiters[key] = callbacks;
            _queue.Add(new IconRequest(path, pixelSize, scale, key, callbacks));
        }
    }

    /// <summary>
    /// 计算缓存键与目标像素尺寸。
    /// size 是布局用的 DIP，真正要渲染的是 物理像素 = DIP × DPI 缩放；
    /// 图标必须按物理像素提供，否则 WPF 会把小位图拉伸到物理像素而发虚。
    /// </summary>
    private static string MakeKey(string path, int size, double dpiScale, out int pixelSize, out double scale)
    {
        var snapped = SnapToStandardSize(size);
        scale = dpiScale > 0 ? dpiScale : SystemDpiScale;
        pixelSize = Math.Max(16, (int)Math.Round(snapped * scale));
        return pixelSize + "|" + scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" + path;
    }

    /// <summary>系统 DPI 缩放（1.0 = 100%）。调用方没有传入窗口缩放时使用。</summary>
    private static double SystemDpiScale
    {
        get
        {
            try
            {
                var dpi = GetDpiForSystem();
                if (dpi > 0) return dpi / 96.0;
            }
            catch
            {
                // 老系统没有该 API，按 100% 处理
            }

            return 1.0;
        }
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var req in _queue.GetConsumingEnumerable())
            {
                try
                {
                    lock (_sync)
                    {
                        if (_disposed) return;
                        if (!_waiters.TryGetValue(req.Key, out var active) || !ReferenceEquals(active, req.Callbacks)) continue;
                    }
                    var img = Safe(() => Extract(req.Path, req.PixelSize, req.Scale)) ?? CreatePlaceholder();
                    List<Action<ImageSource>>? cbs = null;
                    lock (_sync)
                    {
                        if (_disposed) return;
                        if (!_waiters.TryGetValue(req.Key, out var active) || !ReferenceEquals(active, req.Callbacks)) continue;
                        if (img != null)
                        {
                            var bytes = img is BitmapSource bitmap
                                ? (long)bitmap.PixelWidth * bitmap.PixelHeight * 4
                                : (long)req.PixelSize * req.PixelSize * 4;
                            if (bytes <= MaxCachedBytes)
                            {
                                while ((_cachedBytes + bytes > MaxCachedBytes || _cache.Count >= MaxCachedEntries) && _cache.Count > 0) EvictOldest();
                                _cache[req.Key] = img;
                                _entryBytes[req.Key] = bytes;
                                _cachedBytes += bytes;
                                _lastAccess[req.Key] = DateTime.UtcNow.Ticks;
                            }
                        }
                        if (_waiters.TryGetValue(req.Key, out cbs))
                        {
                            _waiters.Remove(req.Key);
                        }
                    }

                    if (cbs != null && img != null)
                    {
                        foreach (var cb in cbs)
                        {
                            System.Windows.Application.Current?.Dispatcher.BeginInvoke(cb, img);
                        }
                    }
                }
                catch
                {
                    lock (_sync)
                    {
                        if (_waiters.TryGetValue(req.Key, out var active) && ReferenceEquals(active, req.Callbacks))
                            _waiters.Remove(req.Key);
                    }
                }
            }
        }
        finally
        {
            if (_jumboImageList is not null) Marshal.ReleaseComObject(_jumboImageList);
            if (_extraLargeImageList is not null) Marshal.ReleaseComObject(_extraLargeImageList);
            _jumboImageList = null;
            _extraLargeImageList = null;
            _queue.Dispose();
        }
    }

    private void EvictOldest()
    {
        string? oldestKey = null;
        long oldestTicks = long.MaxValue;
        foreach (var kv in _lastAccess)
        {
            if (kv.Value < oldestTicks)
            {
                oldestTicks = kv.Value;
                oldestKey = kv.Key;
            }
        }
        if (oldestKey != null)
        {
            _cache.Remove(oldestKey);
            _lastAccess.Remove(oldestKey);
            if (_entryBytes.Remove(oldestKey, out var bytes)) _cachedBytes -= bytes;
            _evictions++;
        }
    }

    /// <summary>
    /// 提取图标主流程。所有分支都先拿到 256px 的源图，最后统一降采样到目标像素尺寸。
    /// 1. .lnk → 系统图像列表（SHIL_JUMBO / SHIL_EXTRALARGE）：
    ///    Shell 已把快捷方式的自定义图标位置与索引解析成图像索引，取到的就是资源管理器里那张图；
    ///    索引低 24 位是图像、高 8 位是 overlay，丢掉高 8 位即可避开合成的小箭头。
    /// 2. .lnk 兜底 → 目标的 IShellItemImageFactory 图标（UWP / 单文件部署常走这里，同样无箭头）。
    /// 3. 其它文件 → IShellItemImageFactory（与资源管理器同源）。
    /// 4. 全部失败 → SHGetFileInfo 大图标 → 占位图。
    /// </summary>
    private static ImageSource Extract(string path, int pixelSize, double dpiScale)
    {
        // 源尺寸取 256：即使只显示 48px，也能拿到最精细的原生图层再降采样。
        // SIIGBF_BIGGERSIZEOK 保证源图不足 256 时 Shell 不做放大，返回的是真实原生图层，
        // 之后统一由 Resample 高质量缩放，避免任何一处在 Shell 侧被低质放大。
        var sourceSize = Math.Max(SourceSize, pixelSize);
        var source = NormalizeIconCanvas(ExtractSource(path, sourceSize));
        return Resample(source, pixelSize, dpiScale);
    }

    // Legacy EXEs can supply a 48px icon inside Shell's 256px canvas.
    // Trim only excessive padding, preserving normal margins and aspect ratio.
    private static BitmapSource NormalizeIconCanvas(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (pixels[(y * width + x) * 4 + 3] < 8) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }

        if (right < left || bottom < top) return source;
        var contentSize = Math.Max(right - left + 1, bottom - top + 1);
        if (contentSize >= Math.Max(width, height) * 0.65) return source;

        var padding = Math.Max(1, (int)Math.Ceiling(contentSize * 0.04));
        left = Math.Max(0, left - padding);
        top = Math.Max(0, top - padding);
        right = Math.Min(width - 1, right + padding);
        bottom = Math.Min(height - 1, bottom + padding);
        var cropped = new CroppedBitmap(source, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
        cropped.Freeze();
        return cropped;
    }

    /// <summary>
    /// 按类型选择最合适的提取通道，返回未经缩放的源图（原生分辨率）。
    /// 每个通道单独捕获异常：个别快捷方式 / shell 扩展可能抛 COM 异常，
    /// 失败即降级到下一通道，绝不让单个坏条目把整个请求丢掉。
    /// </summary>
    private static BitmapSource ExtractSource(string path, int sourceSize)
    {
        var isLink = !string.IsNullOrEmpty(path) && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        if (isLink)
        {
            // 1. 首选 IShellItemImageFactory 直接提取 .lnk：
            //    它能解析快捷方式的自定义图标位置与索引、返回原生高清图层，且不含箭头遮罩。
            //    （优先于系统图像列表 —— JUMBO 缓存里的低清图标已被 Shell 放大过，二次缩放必然发糊。）
            var viaLink = Safe(() => ExtractViaItemFactory(path, sourceSize, IconLinkFlags));
            if (viaLink is not null) return viaLink;

            // 2. 兜底：系统图像列表（个别 shell 扩展只在此通道可用）
            var viaImageList = Safe(() => ExtractViaSystemImageList(path, SHIL_JUMBO))
                               ?? Safe(() => ExtractViaSystemImageList(path, SHIL_EXTRALARGE));
            if (viaImageList is not null) return viaImageList;

            // 3. 兜底：解析 .lnk 目标自身图标
            var target = Safe(() => ResolveLnkTarget(path));
            if (!string.IsNullOrEmpty(target))
            {
                var viaTarget = Safe(() => ExtractViaItemFactory(target!, sourceSize, IconLinkFlags));
                if (viaTarget is not null) return viaTarget;
            }
        }
        else
        {
            var img = Safe(() => ExtractViaItemFactory(path, sourceSize, IconItemFlags));
            if (img is not null) return img;
        }

        return Safe(() => ExtractViaShGetFileInfo(path)) ?? CreatePlaceholder();
    }

    /// <summary>把可能抛 COM 异常的通道包一层，失败返回 null 走下一级。</summary>
    private static T? Safe<T>(Func<T?> action) where T : class
    {
        try
        {
            return action();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 走系统图像列表取图标（与资源管理器同源，且天然支持图标索引）。
    /// HICON 来自 Shell 图标缓存，尺寸由列表档位决定（JUMBO = 256）。
    /// </summary>
    private static BitmapSource? ExtractViaSystemImageList(string path, int imageListSize)
    {
        try
        {
            var info = new SHFILEINFO();
            var result = SHGetFileInfo(path, 0, ref info, Marshal.SizeOf(info), SHGFI_SYSICONINDEX);
            if (result == IntPtr.Zero) return null;

            // 低 24 位是系统图像列表索引，高 8 位是 overlay（快捷方式箭头）索引 —— 这里只要前者
            var index = info.iIcon & 0xFFFFFF;
            if (index < 0) return null;

            var list = GetSystemImageList(imageListSize);
            if (list is null) return null;

            var hicon = IntPtr.Zero;
            if (list.GetIcon(index, ILD_TRANSPARENT, ref hicon) != 0 || hicon == IntPtr.Zero) return null;

            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                DestroyIcon(hicon);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 取系统图像列表接口。Shell 的 IImageList 是 STA 对象，
    /// 按线程缓存（提取线程固定唯一），避免跨套间调用失败。
    /// </summary>
    private static IImageList? GetSystemImageList(int imageListSize)
    {
        var cache = imageListSize == SHIL_JUMBO ? ref _jumboImageList : ref _extraLargeImageList;
        if (cache is not null) return cache;

        var iid = typeof(IImageList).GUID;
        if (SHGetImageList(imageListSize, ref iid, out var list) != 0 || list is null)
        {
            return null;
        }

        cache = list;
        return list;
    }

    /// <summary>解析 .lnk 真实目标路径。</summary>
    private static string? ResolveLnkTarget(string lnkPath)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new CShellLink();
            var store = (IPersistFile)link;
            store.Load(lnkPath, 0);
            var sb = new System.Text.StringBuilder(260);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link is not null) Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>
    /// 把源图高质量重采样到目标像素尺寸，并写入对应 DPI。
    /// 关键点：
    /// 1. 源图来自 Shell 的原生图层（SIIGBF_BIGGERSIZEOK），这里只降不升；源图确实不足时才做单次放大；
    /// 2. 整条流水线全部走 GDI+（System.Drawing），不调用任何 WPF 视觉/渲染 API，
    ///    因此完全线程安全 —— 在后台 STA 线程上也不会卡死或与 UI 线程争用 Dispatcher；
    /// 3. 输出 Pbgra32 带上 DPI 缩放，XAML 里 <c>Stretch="None"</c> 时正好显示为"目标像素尺寸 / DPI 缩放"个 DIP。
    /// </summary>
    private static ImageSource Resample(BitmapSource source, int pixelSize, double dpiScale)
    {
        var dpi = 96.0 * dpiScale;
        if (source.PixelWidth == pixelSize && source.PixelHeight == pixelSize
            && Math.Abs(source.DpiX - dpi) < 0.5 && Math.Abs(source.DpiY - dpi) < 0.5)
        {
            return source;
        }

        // 等比缩放到目标画布（保持宽高比，避免非正方源图被拉变形）
        var scale = Math.Min((double)pixelSize / source.PixelWidth, (double)pixelSize / source.PixelHeight);
        var contentW = Math.Clamp((int)Math.Round(source.PixelWidth * scale), 1, pixelSize);
        var contentH = Math.Clamp((int)Math.Round(source.PixelHeight * scale), 1, pixelSize);

        using var gdiSrc = ToGdiBitmap(source);
        if (gdiSrc is null)
        {
            return source;
        }

        // 逐级对半降采样：避免大比例单次缩放产生摩尔纹/锯齿；放大一律单次高质量插值
        using var gdiScaled = ScaleCrisp(gdiSrc, contentW, contentH);

        if (gdiScaled.Width == pixelSize && gdiScaled.Height == pixelSize)
        {
            return GdiBitmapToPbgra32(gdiScaled, dpi);
        }

        // 非正方源图：居中并垫透明底
        using var canvas = new Bitmap(pixelSize, pixelSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(System.Drawing.Color.Transparent);
            var x = (pixelSize - gdiScaled.Width) / 2;
            var y = (pixelSize - gdiScaled.Height) / 2;
            g.DrawImage(gdiScaled, x, y, gdiScaled.Width, gdiScaled.Height);
        }

        return GdiBitmapToPbgra32(canvas, dpi);
    }

    /// <summary>
    /// 逐级对半降采样：再减半不会低于目标尺寸时就整级减半，
    /// 让最后一次的缩放比落在合理区间，避免一步到位产生摩尔纹/锯齿。
    /// 放大（源图小于目标）一律单次高质量双三次插值。
    /// </summary>
    private static Bitmap ScaleCrisp(Bitmap src, int targetW, int targetH)
    {
        if (src.Width == targetW && src.Height == targetH)
        {
            return CloneBitmap(src);
        }

        var cur = CloneBitmap(src);
        var curW = src.Width;
        var curH = src.Height;

        if (targetW <= curW && targetH <= curH)
        {
            while (curW / 2 >= targetW && curH / 2 >= targetH)
            {
                curW /= 2;
                curH /= 2;
                var next = new Bitmap(curW, curH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(next))
                {
                    SetHighQuality(g);
                    g.DrawImage(cur, 0, 0, curW, curH);
                }
                cur.Dispose();
                cur = next;
            }
        }

        if (cur.Width != targetW || cur.Height != targetH)
        {
            var next = new Bitmap(targetW, targetH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(next))
            {
                SetHighQuality(g);
                g.DrawImage(cur, 0, 0, targetW, targetH);
            }
            cur.Dispose();
            cur = next;
        }

        return cur;
    }

    private static void SetHighQuality(Graphics g)
    {
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    private static Bitmap CloneBitmap(Bitmap src)
    {
        var clone = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(clone))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(src, 0, 0);
        }
        return clone;
    }

    /// <summary>把 WPF BitmapSource 转成 System.Drawing Bitmap（32bpp ARGB）。</summary>
    private static Bitmap? ToGdiBitmap(BitmapSource source)
    {
        var w = source.PixelWidth;
        var h = source.PixelHeight;
        if (w <= 0 || h <= 0) return null;

        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);

        try
        {
            // Explicitly unpremultiply for GDI+; copying Pbgra32 bytes directly
            // darkens translucent edges when they are premultiplied again later.
            var straight = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var srcBytes = new byte[source.PixelWidth * source.PixelHeight * 4];
            straight.CopyPixels(System.Windows.Int32Rect.Empty, srcBytes, source.PixelWidth * 4, 0);

            // WPF Pbgra32 字节序是 BGRA，与 GDI+ 32bppArgb（也是 BGRA）一致 —— 直接拷
            Marshal.Copy(srcBytes, 0, data.Scan0, srcBytes.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return bmp;
    }

    /// <summary>把 GDI+ Bitmap 转为 WPF Pbgra32（预乘 Alpha）BitmapSource，带正确 DPI。</summary>
    private static BitmapSource GdiBitmapToPbgra32(Bitmap bmp, double dpi, bool detectPremultiplication = false)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat);
        var pixels = new byte[data.Stride * bmp.Height];
        try
        {
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        // GDI+ ARGB 是直通 Alpha：若不预乘，WPF Pbgra32 会按"未预乘"渲染 → 半透明边缘发黑/锯齿
        PremultiplyAlphaInPlace(pixels, detectPremultiplication);

        var bmpSource = BitmapSource.Create(bmp.Width, bmp.Height, dpi, dpi, PixelFormats.Pbgra32, null, pixels, data.Stride);
        bmpSource.Freeze();
        return bmpSource;
    }

    /// <summary>
    /// 原地把 BGRA 直通 Alpha 预乘为 BGRA 预乘 Alpha。
    /// 仅在确认为直通 Alpha 时执行（避免重复预乘导致边缘发暗），典型表现是
    /// 存在"半透明像素的 RGB 分量大于 Alpha"——一旦发现就整图预乘。
    /// </summary>
    private static void PremultiplyAlphaInPlace(byte[] px, bool detectPremultiplication = false)
    {
        // Resampled Format32bppArgb is known to be straight alpha, including dark
        // colors whose RGB never exceeds alpha. Only raw Shell bitmaps need detection.
        var straight = !detectPremultiplication;
        for (var i = 0; i + 3 < px.Length; i += 4)
        {
            var a = px[i + 3];
            if (a == 0 || a == 255) continue;
            if (px[i] > a || px[i + 1] > a || px[i + 2] > a)
            {
                straight = true;
                break;
            }
        }

        if (!straight) return;

        for (var i = 0; i + 3 < px.Length; i += 4)
        {
            var a = px[i + 3];
            if (a == 0)
            {
                px[i] = 0; px[i + 1] = 0; px[i + 2] = 0;
                continue;
            }
            if (a == 255) continue;
            px[i] = (byte)(px[i] * a / 255);
            px[i + 1] = (byte)(px[i + 1] * a / 255);
            px[i + 2] = (byte)(px[i + 2] * a / 255);
        }
    }

    /// <summary>走 IShellItemImageFactory（与资源管理器同源）。</summary>
    private static BitmapSource? ExtractViaItemFactory(string path, int size, int flags)
    {
        Guid iid = typeof(IShellItemImageFactory).GUID;
        int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        if (hr != 0 || factory == null) return null;

        try
        {
            var hbmp = IntPtr.Zero;
            hr = factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbmp);
            if (hr != 0 || hbmp == IntPtr.Zero) return null;

            try
            {
                // GDI+ 路径：BGRA 直通 Alpha（与 WPF Pbgra32 字节序一致，无需交换），
                // 显式预乘后交给 WPF，半透明边缘不会出现黑边/锯齿；
                // 整条路径在后台 STA 线程上安全，不需要 Dispatcher。
                using var gdi = System.Drawing.Image.FromHbitmap(hbmp);
                if (gdi is not Bitmap bmp) return null;
                return GdiBitmapToPbgra32(bmp, 96.0, detectPremultiplication: true);
            }
            finally
            {
                DeleteObject(hbmp);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>最后兜底：SHGetFileInfo 大图标（32px，会被放大，仅在上面全部失败时使用）。</summary>
    private static BitmapSource? ExtractViaShGetFileInfo(string path)
    {
        try
        {
            var flags = SHGFI_ICON | SHGFI_LARGEICON;
            if (Directory.Exists(path)) flags |= SHGFI_USEFILEATTRIBUTES;
            var info = new SHFILEINFO();
            var h = SHGetFileInfo(path, 0, ref info, Marshal.SizeOf(info), flags);
            if (h == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource CreatePlaceholder()
    {
        // 纯像素构造的全透明占位图（Pbgra32 全零字节 = 全透明），
        // 不经过 WPF 渲染管线 —— 后台线程直接创建，线程安全。
        var pixels = new byte[PlaceholderSize * PlaceholderSize * 4];
        var src = BitmapSource.Create(PlaceholderSize, PlaceholderSize, 96, 96, PixelFormats.Pbgra32, null, pixels, PlaceholderSize * 4);
        src.Freeze();
        return src;
    }

    private const int PlaceholderSize = 48;

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _waiters.Clear();
            _cache.Clear();
            _lastAccess.Clear();
            _entryBytes.Clear();
            _cachedBytes = 0;
            _queue.CompleteAdding();
        }
    }

    private record IconRequest(string Path, int PixelSize, double Scale, string Key, List<Action<ImageSource>> Callbacks);

    // ===== P/Invoke =====
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    /// <summary>取系统图像列表索引（而非 HICON），用于后续按指定档位取高清图标。</summary>
    private const uint SHGFI_SYSICONINDEX = 0x000004000;

    /// <summary>系统图像列表档位：EXTRALARGE = 48px，JUMBO = 256px。</summary>
    private const int SHIL_EXTRALARGE = 0x02;
    private const int SHIL_JUMBO = 0x04;

    /// <summary>绘制标志：保留 Alpha 通道的透明绘制。</summary>
    private const int ILD_TRANSPARENT = 0x00000001;

    /// <summary>
    /// IShellItemImageFactory 标志：只要图标、不要缩略图。
    /// 快捷方式的目标若是图片，取缩略图会导致同类型条目风格不一致。
    /// </summary>
    private const int SIIGBF_ICONONLY = 0x00000004;

    /// <summary>
    /// 告诉 Shell 不要把小尺寸源图强行拉伸到请求尺寸，而是按原生分辨率返回
    /// （放缩由本类统一高质量完成），避免 Shell 低质放大导致图标发糊。
    /// </summary>
    private const int SIIGBF_BIGGERSIZEOK = 0x00000001;

    /// <summary>.lnk：只要图标、不强制放大，取到最接近 256 的原生图层。</summary>
    private const int IconLinkFlags = SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK;

    /// <summary>普通文件/文件夹：保留缩略图能力，但不强制把小图标拉伸到 256。</summary>
    private const int IconItemFlags = SIIGBF_BIGGERSIZEOK;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, int cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);

    /// <summary>取系统图像列表（EXTRALARGE / JUMBO 档位），与资源管理器共用同一份图标缓存。</summary>
    [DllImport("shell32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    /// <summary>系统 DPI（1.0 = 96 DPI）。用于把布局尺寸换算成物理像素。</summary>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    /// <summary>
    /// 系统图像列表接口。只声明到 GetIcon（vtable 前 8 个槽位）。
    /// IUnknown 派生接口严格按顺序走 vtable，中间任何方法都不能省略 —— 
    /// 这里 Remove 必须占位，否则 GetIcon 会错调到 Remove 而拿不到图标。
    /// </summary>
    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int GetImageCount(ref int pi);
        [PreserveSig] int GetImageInfo(int i, ref IMAGEINFO pImageInfo);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IMAGEINFO
    {
        public IntPtr hbmImage;
        public IntPtr hbmMask;
        public int Unused1;
        public int Unused2;
        public RECT rcImage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
