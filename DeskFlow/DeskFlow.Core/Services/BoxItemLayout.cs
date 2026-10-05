namespace DeskFlow.Core.Services;

/// <summary>按实际视口宽度计算盒子列数；cellWidth 已包含条目的左右间距。</summary>
public static class BoxItemLayout
{
    public static int CalculateColumns(double viewportWidth, double cellWidth)
    {
        if (!double.IsFinite(cellWidth) || cellWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellWidth));
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0) return 1;

        // 只补偿浮点误差；隐藏的滚动条不占空间，不能再扣除固定的预留宽度。
        return Math.Max(1, (int)Math.Min(int.MaxValue, Math.Floor((viewportWidth + 1e-6) / cellWidth)));
    }
}
