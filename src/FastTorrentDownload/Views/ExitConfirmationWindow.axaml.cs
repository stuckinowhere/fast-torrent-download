using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FastTorrentDownload.Views;

public sealed record ExitChoice(bool RememberChoice);

public partial class ExitConfirmationWindow : Window
{
    public ExitConfirmationWindow() => InitializeComponent();

    private void Exit_Click(object? sender, RoutedEventArgs eventArgs) => Close(new ExitChoice(RememberCheckBox.IsChecked == true));
    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs) => Close(null);
}
