using System.Windows;
using System.Windows.Input;
using MauseRecorder.Services;

namespace MauseRecorder.Windows;

public partial class DialogWindow : Window
{
    private DialogWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (InputBox.IsVisible)
            {
                InputBox.Focus();
                InputBox.SelectAll();
            }
            else
            {
                ConfirmButton.Focus();
            }
        };
    }

    public string InputText => InputBox.Text.Trim();

    public static string? Prompt(Window owner, string title, string message, string initialValue = "", string confirmText = "")
    {
        var dialog = new DialogWindow { Owner = owner };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.InputLabel.Visibility = Visibility.Visible;
        dialog.InputBox.Visibility = Visibility.Visible;
        dialog.InputBox.Text = initialValue;
        dialog.ConfirmButton.Content = string.IsNullOrEmpty(confirmText) ? LocalizationService.T("S_Save") : confirmText;
        return dialog.ShowDialog() == true ? dialog.InputText : null;
    }

    public static bool Confirm(Window owner, string title, string message, string confirmText = "", bool danger = false)
    {
        var dialog = new DialogWindow { Owner = owner };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = string.IsNullOrEmpty(confirmText) ? LocalizationService.T("S_Confirm") : confirmText;
        dialog.InputLabel.Visibility = Visibility.Collapsed;
        dialog.InputBox.Visibility = Visibility.Collapsed;
        if (danger)
        {
            dialog.ConfirmButton.Style = (Style)dialog.FindResource("DangerQuietButton");
            dialog.ConfirmButton.Padding = new Thickness(18, 11, 18, 11);
        }
        return dialog.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
        if (e.Key == Key.Enter && InputBox.IsVisible) DialogResult = true;
    }

    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
