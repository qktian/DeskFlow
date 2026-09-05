using System.Runtime.InteropServices;

namespace DeskFlow.Shell;

/// <summary>盒子窗口的原生样式调整。</summary>
/// <remarks>
/// 亚克力背景（SetWindowCompositionAttribute + AccentPolicy）在 WPF 分层窗口上会渲染为纯黑不透明，
/// V1.0 已移除该特性，这里只保留工具窗口标记。
/// </remarks>
public static class WindowEffects
{
    /// <summary>把窗口标记为工具窗口：不出现在任务栏与 Alt+Tab 列表中。</summary>
    public static void MakeToolWindow(IntPtr hwnd)
    {
        try
        {
            var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            var value = style.ToInt64() | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(value));
        }
        catch
        {
            // 忽略
        }
    }
}
