using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FastTorrentDownload.Models;

namespace FastTorrentDownload.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings.Copy();
        InitializeComponent();
        DefaultDownloadFolderTextBox.Text = _settings.DefaultDownloadFolder;
        ListenPortTextBox.Text = _settings.ListenPort.ToString(CultureInfo.InvariantCulture);
        ListenAddressTextBox.Text = _settings.ListenAddress;
        DhtCheckBox.IsChecked = _settings.EnableDht;
        PexCheckBox.IsChecked = _settings.EnablePeerExchange;
        LsdCheckBox.IsChecked = _settings.EnableLocalPeerDiscovery;
        PortForwardingCheckBox.IsChecked = _settings.EnablePortForwarding;
        EncryptionCheckBox.IsChecked = _settings.RequireEncryptedPeerConnections;
        DownloadLimitTextBox.Text = _settings.MaximumDownloadKiBPerSecond.ToString(CultureInfo.InvariantCulture);
        UploadLimitTextBox.Text = _settings.MaximumUploadKiBPerSecond.ToString(CultureInfo.InvariantCulture);
        SeedRatioTextBox.Text = _settings.SeedRatioTarget.ToString(CultureInfo.InvariantCulture);
        ThemeComboBox.SelectedIndex = _settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
    }

    private async void ChooseFolder_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose default download folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            DefaultDownloadFolderTextBox.Text = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (!int.TryParse(ListenPortTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var listenPort) ||
            listenPort is < 1024 or > 65535)
        {
            ShowValidationError("Port must be between 1024 and 65535.");
            return;
        }

        try
        {
            FastTorrentDownload.Services.NetworkBinding.ResolveListenAddress(ListenAddressTextBox.Text);
        }
        catch (ArgumentException exception)
        {
            ShowValidationError(exception.Message);
            return;
        }

        if (!int.TryParse(DownloadLimitTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var downloadLimit) || downloadLimit < 0 ||
            !int.TryParse(UploadLimitTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uploadLimit) || uploadLimit < 0 ||
            !double.TryParse(SeedRatioTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) || ratio < 0)
        {
            ShowValidationError("Use non-negative numeric limits.");
            return;
        }

        _settings.DefaultDownloadFolder = DefaultDownloadFolderTextBox.Text ?? string.Empty;
        _settings.ListenPort = listenPort;
        _settings.ListenAddress = ListenAddressTextBox.Text?.Trim() ?? string.Empty;
        _settings.EnableDht = DhtCheckBox.IsChecked == true;
        _settings.EnablePeerExchange = PexCheckBox.IsChecked == true;
        _settings.EnableLocalPeerDiscovery = LsdCheckBox.IsChecked == true;
        _settings.EnablePortForwarding = PortForwardingCheckBox.IsChecked == true;
        _settings.RequireEncryptedPeerConnections = EncryptionCheckBox.IsChecked == true;
        _settings.MaximumDownloadKiBPerSecond = downloadLimit;
        _settings.MaximumUploadKiBPerSecond = uploadLimit;
        _settings.SeedRatioTarget = ratio;
        _settings.Theme = ThemeComboBox.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
        _settings.Normalize();
        Close(_settings);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs) => Close(null);

    private void ShowValidationError(string message)
    {
        ValidationErrorTextBlock.Text = message;
        ValidationErrorTextBlock.IsVisible = true;
    }
}
