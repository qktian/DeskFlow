using System.Runtime.InteropServices;

// 诊断 IShellItemImageFactory::GetImage 返回的 HBITMAP 的透明通道格式：
// 统计每个图标的半透明边缘像素，检查 RGB 是否超过 Alpha（预乘数据不可能超过）。
// 结论：违反不变量 => 直通 Alpha（需手动预乘）；满足不变量 => 已是预乘。

if (args.Length == 0)
{
    Console.WriteLine("用法: IconDiag <文件路径...>");
    return;
}

foreach (var path in args)
{
    Analyze(path);
}

static void Analyze(string rawPath)
{
    // Shell API 不接受正斜杠路径
    var path = rawPath.Replace('/', '\\');
    try
    {
        var guid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
        Shell.SHCreateItemFromParsingName(path, IntPtr.Zero, ref guid, out var factory);

        var hr = factory.GetImage(new Shell.SIZE { Width = 64, Height = 64 },
            Shell.SIIGBF_ICONONLY | Shell.SIIGBF_BIGGERSIZEOK, out var hbm);

        if (hr != 0 || hbm == IntPtr.Zero)
        {
            Console.WriteLine($"{Path.GetFileName(path)}: GetImage 失败 hr=0x{hr:X}");
            return;
        }

        if (Shell.GetObject(hbm, Marshal.SizeOf<Shell.BITMAP>(), out var bm) == 0)
        {
            Console.WriteLine($"{Path.GetFileName(path)}: GetObject 失败");
            return;
        }

        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight), stride = w * 4;
        var px = new byte[stride * h];

        var bmi = new Shell.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Shell.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32
        };

        var hdc = Shell.GetDC(IntPtr.Zero);
        _ = Shell.GetDIBits(hdc, hbm, 0, (uint)h, px, ref bmi, 0);
        _ = Shell.ReleaseDC(IntPtr.Zero, hdc);
        Shell.DeleteObject(hbm);

        int semiTransparent = 0, violatesPremult = 0, opaque = 0, transparent = 0;
        var samples = new List<string>();

        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];

            if (a == 0) { transparent++; continue; }
            if (a == 255) { opaque++; continue; }

            semiTransparent++;
            if (r > a || g > a || b > a)
            {
                violatesPremult++;
                if (samples.Count < 4)
                {
                    samples.Add($"A={a} R={r} G={g} B={b}");
                }
            }
        }

        Console.WriteLine($"{Path.GetFileName(path)}: 尺寸={w}x{h} 透明={transparent} 不透明={opaque} " +
                          $"半透明={semiTransparent} 其中RGB超Alpha(直通Alpha证据)={violatesPremult}");
        foreach (var s in samples)
        {
            Console.WriteLine($"    违例样本: {s}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{Path.GetFileName(path)}: 异常 {ex.Message}");
    }
}

internal static class Shell
{
    public const uint SIIGBF_BIGGERSIZEOK = 0x1;
    public const uint SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int GetObject(IntPtr hObject, int cBytes, out BITMAP bm);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);
}
