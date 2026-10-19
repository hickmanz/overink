using System.Windows;

namespace OverInk.Editor;

public partial class RenameCaptureDialog : Window
{
    public RenameCaptureDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        NameBox.SelectAll();
        Loaded += (_, _) => NameBox.Focus();
    }

    public string? NewName { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        NewName = NameBox.Text.Trim();
        DialogResult = true;
    }
}
