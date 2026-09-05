using System.Windows;

namespace DeskFlow.App;

public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string defaultValue)
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;
        PromptText.Text = prompt;
        InputBox.Text = defaultValue;

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public string InputText => InputBox.Text;

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
