using System.Runtime.InteropServices;

namespace DeskFlow.Shell;

public static class BoundedWindowMessage
{
    public const uint TimeoutMilliseconds = 250;
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out UIntPtr result);

    public static bool TrySend(IntPtr hwnd, uint message, IntPtr wParam, out int error, uint timeout = TimeoutMilliseconds)
    {
        // BLOCK prevents UI reentrancy, ABORTIFHUNG and ERRORONEXIT bound unhealthy Explorer calls.
        const uint flags = 0x0001 | 0x0002 | 0x0020;
        Marshal.SetLastPInvokeError(0);
        var sent = SendMessageTimeout(hwnd, message, wParam, IntPtr.Zero, flags, Math.Clamp(timeout, 1, 1000), out _);
        error = sent != IntPtr.Zero ? 0 : Marshal.GetLastPInvokeError();
        return sent != IntPtr.Zero;
    }
}
