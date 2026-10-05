using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DeskFlow.Core.Services;

namespace DeskFlow.Shell;

/// <summary>文件操作：打开、定位、复制路径、移入回收站。</summary>
public static class FileOperationService
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pTo;

        public ushort fFlags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;

        public IntPtr hNameMappings;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    /// <summary>回收站统计（root 传 null 表示所有盘符）：项目数与总字节数。</summary>
    public static (int Items, long Bytes) GetRecycleBinInfo()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref info) == 0
                ? ((int)info.i64NumItems, info.i64Size)
                : (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// 清空所有盘符的回收站。跳过系统确认框（由调用方自行确认），
    /// 保留系统进度框让用户看到执行过程。返回是否成功。
    /// </summary>
    public static bool EmptyRecycleBin()
    {
        try
        {
            const uint flags = SHERB_NOCONFIRMATION | SHERB_NOSOUND;
            return SHEmptyRecycleBin(IntPtr.Zero, null, flags) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"无法打开：{path}\n{ex.Message}", "DeskFlow");
        }
    }

    public static void ShowInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }

    public static bool MoveToRecycleBin(string path)
    {
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
            };
            var result = SHFileOperation(ref op);
            if (op.fAnyOperationsAborted) return false;
            if (result != 0) throw new IOException($"系统错误代码：0x{result:X}");
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"删除失败：{ex.Message}", "DeskFlow");
            return false;
        }
    }

    public static string? Rename(string path, string newName)
    {
        try
        {
            var isDirectory = Directory.Exists(path);
            var target = FileNamePolicy.RenameTarget(path, newName, isDirectory);
            if (isDirectory)
            {
                Directory.Move(path, target);
            }
            else
            {
                File.Move(path, target);
            }

            return target;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"重命名失败：{ex.Message}", "DeskFlow");
            return null;
        }
    }

    public static void CopyToClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>回收站 CLSID 的 GUID 字符串。</summary>
    private const string RecycleBinClsid = "645FF040-5081-101B-9F08-00AA002F954E";

    /// <summary>快捷方式另一种常见写法：explorer.exe shell:RecycleBinFolder。</summary>
    private const string RecycleBinShellUri = "shell:RecycleBinFolder";

    /// <summary>回收站 CLSID 的 IDList 二进制形式（GUID 小端字节序）。</summary>
    private static readonly byte[] RecycleBinClsidBytes =
    {
        0x40, 0xF0, 0x5F, 0x64, 0x81, 0x50, 0x1B, 0x10,
        0x9F, 0x08, 0x00, 0xAA, 0x00, 0x2F, 0x95, 0x4E
    };

    /// <summary>
    /// 判断一个 .lnk 是否指向回收站。
    /// 回收站不是文件路径，快捷方式以 IDList 存储目标（含回收站 CLSID 的二进制），
    /// 个别生成器会写明文字符串形式，两种都检测。
    /// </summary>
    public static bool IsRecycleBinShortcut(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            return ContainsSequence(bytes, RecycleBinClsidBytes) ||
                   ContainsSequence(bytes, Encoding.Unicode.GetBytes(RecycleBinClsid)) ||
                   ContainsSequence(bytes, Encoding.ASCII.GetBytes(RecycleBinClsid)) ||
                   ContainsSequence(bytes, Encoding.Unicode.GetBytes(RecycleBinShellUri)) ||
                   ContainsSequence(bytes, Encoding.ASCII.GetBytes(RecycleBinShellUri));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>朴素子序列查找（lnk 通常小于几 KB，无需优化）。</summary>
    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
