using CommunityToolkit.Mvvm.ComponentModel;
using FastTorrentDownload.Models;
using FastTorrentDownload.Services;

namespace FastTorrentDownload.ViewModels;

public sealed partial class TorrentRowViewModel(TorrentSnapshot snapshot) : ObservableObject
{
    public string Id { get; } = snapshot.Id;
    [ObservableProperty] private string _name = snapshot.Name;
    [ObservableProperty] private string _destination = snapshot.Destination;
    [ObservableProperty] private string _state = snapshot.State;
    [ObservableProperty] private double _progress = snapshot.Progress;
    [ObservableProperty] private string? _operation = snapshot.Operation;
    public bool IsBusy => Operation is not null || State is "Starting" or "Stopping";
    public bool CanAct => !IsBusy;
    // "Queued" torrents are stopped by the scheduler, not by the user, so Pause is the way to hold them.
    public bool CanPause => CanAct && State is "Downloading" or "Seeding" or "Metadata" or "Hashing" or "FetchingHashes" or "Queued" or "Stalled";
    public bool CanResume => CanAct && !CanPause;
    public string StateLabel => Operation ?? State;
    // Engine state names are developer vocabulary; the chip speaks the user's.
    public string StateDisplay => Operation ?? State switch
    {
        "Metadata" => "Fetching file list",
        "Hashing" or "FetchingHashes" => "Checking files",
        "HashingPaused" => "Check paused",
        "Stopped" or "Paused" => Progress >= 100 ? "Complete" : "Paused",
        _ => State
    };
    public bool IsRunningState => Operation is null && State is "Downloading" or "Seeding" or "Metadata" or "Hashing" or "FetchingHashes" or "Starting";
    public bool IsStalledState => Operation is null && State == "Stalled";
    public bool IsErrorState => Operation is null && State == "Error";
    [ObservableProperty] private bool _isDetailsOpen;
    public string DetailsToggleLabel => IsDetailsOpen ? "Hide details" : "Details";
    partial void OnIsDetailsOpenChanged(bool value) => OnPropertyChanged(nameof(DetailsToggleLabel));
    partial void OnOperationChanged(string? value) => NotifyActionState();
    partial void OnStateChanged(string value) => NotifyActionState();

    private void NotifyActionState()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(StateDisplay));
        OnPropertyChanged(nameof(IsRunningState));
        OnPropertyChanged(nameof(IsStalledState));
        OnPropertyChanged(nameof(IsErrorState));
        OnPropertyChanged(nameof(DetailLabel));
        OnPropertyChanged(nameof(DiagnosticsText));
    }
    private long _downloadRate = snapshot.DownloadRate;
    private long _uploadRate = snapshot.UploadRate;
    private long _downloadedBytes = snapshot.DownloadedBytes;
    private long _uploadedBytes = snapshot.UploadedBytes;
    private int _seederCount = snapshot.SeederCount;
    private int _leecherCount = snapshot.LeecherCount;
    private long _selectedBytes = snapshot.SelectedBytes;
    private long _verifiedBytes = snapshot.VerifiedBytes;
    private TorrentDiagnostics? _diagnostics = snapshot.Diagnostics;

    public string ProgressLabel => $"{Progress:0.0}%";
    public string TransferLabel => $"↓ {FormatRate(_downloadRate)}   ↑ {FormatRate(_uploadRate)}";
    // A stalled row says why instead of an ETA that will never arrive.
    private string ProgressOutlook => State switch
    {
        "Queued" => "waiting for a free download slot",
        "Stopped" or "Paused" => _selectedBytes > 0 && _verifiedBytes >= _selectedBytes ? "Complete" : "paused",
        _ => _diagnostics?.StallSummary ?? TransferFormatting.Remaining(_verifiedBytes, _selectedBytes, _downloadRate, State)
    };
    // Peer counts and an ETA mean nothing for a torrent that is not running.
    private bool ShowsPeers => State is not ("Stopped" or "Paused" or "Queued" or "Error");
    private string PeerSummary => _diagnostics?.PeerError is not null
        ? "peer statistics unavailable"
        : $"{_seederCount} connected seeders · {_leecherCount} other peers";
    public string DetailLabel => $"{TransferFormatting.Bytes(_verifiedBytes)} / {TransferFormatting.Bytes(_selectedBytes)} verified · {ProgressOutlook}{(ShowsPeers ? " · " + PeerSummary : string.Empty)}";
    public string SessionLabel => $"Session received {TransferFormatting.Bytes(_downloadedBytes)} · sent {TransferFormatting.Bytes(_uploadedBytes)}";
    public string DiagnosticsText => $"Torrent state: {StateLabel}\n{_diagnostics?.Describe(_downloadRate) ?? "Diagnostics not available yet."}";

    public void Update(TorrentSnapshot snapshot)
    {
        Name = snapshot.Name;
        Destination = snapshot.Destination;
        State = snapshot.State;
        Operation = snapshot.Operation;
        Progress = snapshot.Progress;
        _downloadRate = snapshot.DownloadRate;
        _uploadRate = snapshot.UploadRate;
        _downloadedBytes = snapshot.DownloadedBytes;
        _uploadedBytes = snapshot.UploadedBytes;
        _seederCount = snapshot.SeederCount;
        _leecherCount = snapshot.LeecherCount;
        _selectedBytes = snapshot.SelectedBytes;
        _verifiedBytes = snapshot.VerifiedBytes;
        _diagnostics = snapshot.Diagnostics;
        OnPropertyChanged(nameof(TransferLabel));
        OnPropertyChanged(nameof(DetailLabel));
        OnPropertyChanged(nameof(SessionLabel));
        OnPropertyChanged(nameof(DiagnosticsText));
    }

    partial void OnProgressChanged(double value)
    {
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(StateDisplay));
    }

    public static string FormatRate(long bytesPerSecond) => TransferFormatting.Rate(bytesPerSecond);
}
