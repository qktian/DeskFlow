using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

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

    public static void MoveToRecycleBin(string path)
    {
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
            };
            SHFileOperation(ref op);
        }
        catch
        {
            // 忽略
        }
    }

    public static bool Rename(string path, string newName)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var dir = new DirectoryInfo(path);
                dir.MoveTo(Path.Combine(dir.Parent?.FullName ?? Path.GetPathRoot(path) ?? ".", newName));
            }
            else
            {
                var file = new FileInfo(path);
                var directory = file.DirectoryName ?? ".";
                var extension = file.Extension;
                var target = Path.Combine(directory, newName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                    ? newName
                    : newName + extension);
                file.MoveTo(target);
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"重命名失败：{ex.Message}", "DeskFlow");
            return false;
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
}
