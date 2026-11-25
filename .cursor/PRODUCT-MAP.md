# Product map

## Run

- Build: `dotnet build FastTorrentDownload.sln -c Release`.
- Regression checks: `dotnet run --project tests/FastTorrentDownload.Tests -c Release`.
- Isolated native UI checks: append `-- --ui-smoke-auto` to the regression command.

## Surfaces

| Surface | Entry points | Verify |
| --- | --- | --- |
| Add torrent and choose files | `Services/TorrentEngineService.cs`, `Services/PathGuard.cs` | Both torrent files and magnets validate paths before payload registration; preview writes no payload and creates no folder; duplicates are rejected. |
| Queue and transfer controls | `ViewModels/MainViewModel.cs`, `Views/MainWindow.axaml` | Per-row actions; pause, removal and automatic completion preserve downloaded files and survive restart; file reselection and moves persist; `MaximumActiveDownloads` queues the rest. |
| Second launch / shell integration | `Program.cs`, `Services/SingleInstanceChannel.cs`, `installer/fast-torrent-download.iss` | A second instance forwards magnet/.torrent args over the named pipe and exits; the installer registers the handlers per user. |
| Stalled downloads | `Services/TorrentEngineService.cs` (`IsStalled`, `ScrapeSwarmAsync`), `Models/TorrentDiagnostics.cs` (`StallSummary`) | No payload for `StallThreshold` shows "Stalled" with the reason; scrape counts name a dead swarm; stalled torrents do not hold the sleep guard or a queue slot. |
| In-order downloads | `Services/TorrentEngineService.cs` (`SetSequentialAsync`), `Views/SelectTorrentFilesWindow.axaml` | Linear picking persists per torrent; switching a running torrent stops, switches and restarts it. |
| IPv6 | `Services/TorrentEngineService.cs` (`CreateListenEndPoints`) | IPv6 listener only when enabled, supported and not pinned to one IPv4 address; loopback `::1` transfer passes. |
| Localization | `Services/Loc.cs`, `Services/Localization/es.txt`, `scripts/Generate-Localization.py` | English is the key; every shown string has a Spanish entry; state identifiers stay English in logic. |
| Privacy | `Models/AppSettings.cs` (`UsePublicMetadataCaches`) | Public .torrent caches are contacted only when the setting is on. |
| Phone alerts | `Services/TelegramNotifier.cs`, `Services/SecretProtector.cs`, `ViewModels/MainViewModel.cs` (`SendPhoneAlert`) | Off by default; token DPAPI-encrypted in settings and never logged; alerts on completion and, optionally, stall or error; failures explained without the token. |
| Deselected files | `Services/SkippedFileWriter.cs`, `Services/TorrentEngineService.cs` (`ApplyFileSelectionAsync`, `RevealSkipped`) | Data of deselected files goes to `cache/skipped`, never the download folder; revealed only on explicit selection; survives restart, recheck and move; removed when the torrent leaves the queue. |
| File creation | `Services/TorrentEngineService.cs` (`CreateEngineSettings`, `FileCreationMode`) | Payload files are sparse: no zero-fill before the first block of a large file. |

## Do not ship without

- Path traversal cases rejected, including mixed separators and Windows drive/stream paths.
- Loopback magnet metadata and selected-file transfers pass, with no payload written during preview.
- Native UI fixture checks pass without using the installed application's data.
