using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using FastTorrentDownload.Services;

namespace FastTorrentDownload.Views;

public partial class PortForwardingGuideWindow : Window
{
    private readonly LocalNetworkGuideInfo _info;

    public PortForwardingGuideWindow(LocalNetworkGuideInfo info)
    {
        _info = info;
        InitializeComponent();
        DeviceLineTextBlock.Text = $"PC / Adapter : {info.HostName} ({info.AdapterName} · MAC {info.MacAddress})";
        IpLineTextBlock.Text = $"Internal IP  : {info.LocalIpv4}";
        PortLineTextBlock.Text = $"Port & Proto : {info.ListenPort} (TCP & UDP / Both)";
        GatewayLineTextBlock.Text = $"Router IP    : {info.GatewayIpv4}";
        StepTwoTextBlock.Text = $"2. Enter Internal IP '{info.LocalIpv4}' (make sure to pick the active {info.AdapterName} IP, not an inactive Wi-Fi IP).";
        StepThreeTextBlock.Text = $"3. Set External Port = {info.ListenPort}, Internal Port = {info.ListenPort}, Protocol = TCP & UDP (Both), and Save.";
    }

    private async void CopyIp_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (Clipboard is not null)
        {
            await Clipboard.SetTextAsync(_info.LocalIpv4);
            CopiedStatusTextBlock.Text = $"Copied Internal IP {_info.LocalIpv4} to clipboard.";
            CopiedStatusTextBlock.IsVisible = true;
        }
    }

    private async void CopyPort_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (Clipboard is not null)
        {
            await Clipboard.SetTextAsync(_info.ListenPort.ToString());
            CopiedStatusTextBlock.Text = $"Copied port {_info.ListenPort} to clipboard.";
            CopiedStatusTextBlock.IsVisible = true;
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs eventArgs) => Close();
}
