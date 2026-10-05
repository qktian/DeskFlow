using DeskFlow.Core.Services;

namespace DeskFlow.Shell;

/// <summary>
/// 控制"系统桌面真实图标"的显示与隐藏（图标收编）。
/// 通过向 SHELLDLL_DefView 发送 WM_COMMAND(0x7402) 触发系统自带的切换命令，
/// 不修改注册表、不注入 explorer.exe，属于可逆的低风险做法。
/// </summary>
public static class DesktopIconService
{
    private const uint ToggleCommandId = 0x7402;
    private static DateTime _lastFailureLogged;

    public static bool AreIconsVisible()
    {
        var listView = FindDesktopListView(out _);
        return listView != IntPtr.Zero && NativeMethods.IsWindowVisible(listView);
    }

    public static void SetIconsVisible(bool visible)
    {
        try
        {
            TrySetIconsVisible(visible);
        }
        catch
        {
            // 切换失败不影响程序运行，用户可在设置中重试
        }
    }

    /// <summary>
    /// 切换一次并校验结果，返回是否已达到目标状态。
    /// 状态已经符合时不会发送切换命令，因此可安全地在守护循环中反复调用。
    /// </summary>
    public static bool TrySetIconsVisible(bool visible)
    {
        var listView = FindDesktopListView(out var shellView);
        if (listView == IntPtr.Zero || shellView == IntPtr.Zero)
        {
            // explorer 尚未创建桌面窗口（开机自启时常见）
            return false;
        }

        if (NativeMethods.IsWindowVisible(listView) == visible) return true;

        if (!BoundedWindowMessage.TrySend(shellView, NativeMethods.WM_COMMAND, (IntPtr)ToggleCommandId, out var error))
        {
            if (DateTime.UtcNow - _lastFailureLogged > TimeSpan.FromSeconds(30))
            {
                _lastFailureLogged = DateTime.UtcNow;
                DiagnosticLog.Error("DesktopIcons", $"Explorer command timed out or failed; Win32={error}");
            }
            return false;
        }
        var currentList = FindDesktopListView(out _);
        return currentList != IntPtr.Zero && NativeMethods.IsWindowVisible(currentList) == visible;
    }

    /// <summary>
    /// 带重试的同步切换。用于退出阶段等必须确保结果、且可接受短暂阻塞的场景。
    /// </summary>
    public static bool SetIconsVisibleWithRetry(bool visible, int attempts = 4, int delayMs = 250)
    {
        attempts = Math.Clamp(attempts, 1, 4);
        delayMs = Math.Clamp(delayMs, 0, 250);
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (TrySetIconsVisible(visible))
                {
                    return true;
                }
            }
            catch
            {
                // 继续重试
            }

            if (i < attempts - 1)
            {
                Thread.Sleep(delayMs);
            }
        }

        return false;
    }

    public static IntPtr FindDesktopListView(out IntPtr shellDefView)
    {
        shellDefView = IntPtr.Zero;

        var progman = NativeMethods.FindWindow("Progman", "Program Manager");
        if (progman != IntPtr.Zero)
        {
            var view = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero)
            {
                var list = NativeMethods.FindWindowEx(view, IntPtr.Zero, "SysListView32", "FolderView");
                if (list != IntPtr.Zero)
                {
                    shellDefView = view;
                    return list;
                }
            }
        }

        // 使用幻灯片壁纸或某些系统版本时，桌面窗口层级会变成 WorkerW 分支
        var foundList = IntPtr.Zero;
        var foundShell = IntPtr.Zero;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            var worker = NativeMethods.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (worker == IntPtr.Zero)
            {
                return true;
            }

            var list = NativeMethods.FindWindowEx(worker, IntPtr.Zero, "SysListView32", "FolderView");
            if (list == IntPtr.Zero)
            {
                return true;
            }

            foundShell = worker;
            foundList = list;
            return false;
        }, IntPtr.Zero);

        shellDefView = foundShell;
        return foundList;
    }

    /// <summary>判断指定点是否位于系统桌面区域（未被任何应用窗口遮挡）。</summary>
    public static bool IsPointOnDesktop(NativeMethods.POINT point)
    {
        var hWnd = NativeMethods.WindowFromPoint(point);
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        var className = NativeMethods.GetClassName(hWnd);
        return className is "SysListView32" or "SHELLDLL_DefView" or "Progman" or "WorkerW";
    }
}
