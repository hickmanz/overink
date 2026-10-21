using System.Windows;
using OverInk.Services;

namespace OverInk.Toolbar;

public partial class UpdatePromptWindow : Window
{
    public UpdatePromptWindow(string message, string confirmText, string? cancelText, string? notes = null)
    {
        InitializeComponent();
        MessageText.Text = message;
        if (!string.IsNullOrWhiteSpace(notes))
        {
            NotesText.Text = notes;
            NotesPanel.Visibility = Visibility.Visible;
        }

        ConfirmButton.Content = confirmText;
        if (string.IsNullOrEmpty(cancelText))
        {
            CancelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            CancelButton.Content = cancelText;
        }

        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
