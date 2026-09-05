using System.Windows;
using DeskFlow.Core.Models;

namespace DeskFlow.App;

/// <summary>编辑待办面板的外观（背景色 / 文字色 / 不透明度 / 圆角）。</summary>
public partial class TodoAppearanceWindow : Window
{
    private readonly TodoBoardModel _model;

    public TodoAppearanceWindow(TodoBoardModel model)
    {
        InitializeComponent();
        _model = model;
        var s = model.Style;

        BgColorBox.Text = s.BackgroundColor;
        TextColorBox.Text = s.ForegroundColor;
        OpacitySlider.Value = Math.Clamp(s.Opacity * 100, 20, 100);
        RadiusSlider.Value = Math.Clamp(s.CornerRadius, 0, 24);
        OpacityValue.Text = $"{(int)OpacitySlider.Value}%";
        RadiusValue.Text = ((int)RadiusSlider.Value).ToString();
    }

    private void Slider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValue is null || RadiusValue is null)
        {
            return;
        }

        OpacityValue.Text = $"{(int)OpacitySlider.Value}%";
        RadiusValue.Text = ((int)RadiusSlider.Value).ToString();
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        var s = _model.Style;
        s.BackgroundColor = string.IsNullOrWhiteSpace(BgColorBox.Text) ? "#33000000" : BgColorBox.Text.Trim();
        s.ForegroundColor = string.IsNullOrWhiteSpace(TextColorBox.Text) ? "#FFFFFF" : TextColorBox.Text.Trim();
        s.Opacity = OpacitySlider.Value / 100.0;
        s.CornerRadius = (int)RadiusSlider.Value;
        DialogResult = true;
    }
}
