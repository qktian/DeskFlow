using DeskFlow.Core.Services;

// Cross-platform tests for the production column calculation; no WPF or user files.
var checks = 0;
void Expect(double width, double cell, int expected, string name)
{
    checks++;
    var actual = BoxItemLayout.CalculateColumns(width, cell);
    if (actual != expected) throw new InvalidOperationException($"{name}: {actual} instead of {expected}");
}

// 32 DIP icon + 36 DIP label space + 3 DIP margin on each side = 74 DIP per cell.
Expect(466, 74, 6, "Screenshot-sized viewport");
Expect(459, 74, 6, "Shrinking the excess right space must not drop a column");
Expect(444, 74, 6, "Six cells fit exactly, with no phantom scrollbar reservation");
Expect(443, 74, 5, "One DIP below the real boundary wraps");
Expect(444 - 1e-8, 74, 6, "Floating-point boundary noise");
Expect(517, 74, 6, "Wider cells stay on six columns until the seventh fits");
Expect(518, 74, 7, "Seventh column fits at its actual boundary");
Expect(0, 74, 1, "Unmeasured viewport");
Expect(double.NaN, 74, 1, "Invalid viewport is safe");
Expect(160, 170, 1, "Very large icons still keep one column");

foreach (var icon in new[] { 16, 24, 32, 48, 64, 96, 128, 256 })
foreach (var columns in new[] { 2, 3, 6, 10 })
foreach (var dpi in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
{
    var cell = icon + 42d;
    var boundary = columns * cell;
    Expect(boundary, cell, columns, $"Exact fit: icon {icon}, columns {columns}, DPI {dpi}");
    Expect(boundary - 1 / dpi, cell, columns - 1, $"One physical pixel too narrow: {icon}/{columns}/{dpi}");
    Expect(boundary + 1 / dpi, cell, columns, $"One physical pixel extra: {icon}/{columns}/{dpi}");
}
Console.WriteLine($"PASS {checks} column-layout checks (production BoxItemLayout).");
