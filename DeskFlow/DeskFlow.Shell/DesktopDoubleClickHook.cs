using System.Runtime.InteropServices;

namespace DeskFlow.Shell;

/// <summary>
/// 全局低级鼠标钩子，用于实现"双击桌面空白处隐藏/显示全部盒子"。
/// 只在光标位于系统桌面（未被任何应用窗口、盒子窗口遮挡）时触发。
/// </summary>
public sealed class DesktopDoubleClickHook : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    private NativeMethods.HookProc? _hookProc;
    private bool _disposed;

    public event EventHandler? DesktopDoubleClicked;

    public bool IsRunning => _hookId != IntPtr.Zero;

    public void Start()
    {
        if (_hookId != IntPtr.Zero)
        {
            return;
        }

        _hookProc = HookCallback;
        var moduleHandle = NativeMethods.GetModuleHandle(null);
        _hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _hookProc, moduleHandle, 0);
    }

    public void Stop()
    {
        if (_hookId == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
        _hookProc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)NativeMethods.WM_LBUTTONDBLCLK)
        {
            try
            {
                var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                if (DesktopIconService.IsPointOnDesktop(hookStruct.pt))
                {
                    DesktopDoubleClicked?.Invoke(this, EventArgs.Empty);
                }
            }
            catch
            {
                // 钩子回调中不允许抛出，忽略单次异常
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }
}
