# Product map

## Run

- Build: `dotnet build FastTorrentDownload.sln -c Release`.
- Regression checks: `dotnet run --project tests/FastTorrentDownload.Tests -c Release`.
- Isolated native UI checks: append `-- --ui-smoke-auto` to the regression command.

## Surfaces

| Surface | Entry points | Verify |
| --- | --- | --- |
| Add torrent and choose files | `Services/TorrentEngineService.cs`, `Services/PathGuard.cs` | Both torrent files and magnets validate paths before payload registration; preview writes no payload. |
| Queue and transfer controls | `ViewModels/MainViewModel.cs` | Pause, removal and automatic completion preserve downloaded files and survive restart. |

## Do not ship without

- Path traversal cases rejected, including mixed separators and Windows drive/stream paths.
- Loopback magnet metadata and selected-file transfers pass, with no payload written during preview.
- Native UI fixture checks pass without using the installed application's data.
