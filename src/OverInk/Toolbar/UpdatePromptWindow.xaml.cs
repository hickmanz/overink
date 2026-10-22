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
        // CenterScreen would always pick one monitor; open where the user is working instead.
        var monitor = MonitorHelper.GetMonitorAtCursor();
        Loaded += (_, _) => WindowPlacement.CenterOnMonitor(this, monitor);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
