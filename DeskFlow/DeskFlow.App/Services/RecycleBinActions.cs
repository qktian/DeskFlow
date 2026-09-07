using System.Windows;
using DeskFlow.Shell;

namespace DeskFlow.App.Services;

/// <summary>
/// 回收站操作：统计 → 确认 → 清空 → 反馈。
/// 供盒子右键菜单与托盘菜单共用（系统桌面图标被收编后，回收站只能从这里访问）。
/// </summary>
public static class RecycleBinActions
{
    public static void ShowEmptyRecycleBinDialog()
    {
        var (items, bytes) = FileOperationService.GetRecycleBinInfo();
        if (items <= 0)
        {
            MessageBox.Show("回收站是空的。", "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var size = bytes >= 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0:F1} MB"
            : $"{Math.Max(1, bytes / 1024):N0} KB";

        if (MessageBox.Show(
                $"确定清空回收站吗？\n\n共 {items} 项，约 {size}\n清空后无法恢复。",
                "DeskFlow", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        if (FileOperationService.EmptyRecycleBin())
        {
            MessageBox.Show($"已清空回收站（{items} 项）。", "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("清空回收站失败，请检查是否有文件被占用后重试。", "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
