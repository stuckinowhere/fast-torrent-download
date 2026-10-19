# fast torrent download

[![CI](https://github.com/stuckinowhere/fast-torrent-download/actions/workflows/ci.yml/badge.svg)](https://github.com/stuckinowhere/fast-torrent-download/actions/workflows/ci.yml)
[![CodeQL](https://github.com/stuckinowhere/fast-torrent-download/actions/workflows/codeql.yml/badge.svg)](https://github.com/stuckinowhere/fast-torrent-download/actions/workflows/codeql.yml)
[![Release](https://img.shields.io/github/v/release/stuckinowhere/fast-torrent-download)](https://github.com/stuckinowhere/fast-torrent-download/releases/latest)

A local-first Windows 11 x64 BitTorrent client built with Avalonia and MonoTorrent. Add magnet links or `.torrent` files, choose exactly which files to download, and manage the queue without accounts, ads, or telemetry. Use it only for content you have the right to share or download.

## What works now

- Add a user-provided magnet link or `.torrent` file to a chosen download folder.
- Select or deselect individual files before starting a torrent.
- Start, pause, resume, recheck, remove from queue (while keeping downloaded files), and open the download folder.
- Persist confirmed additions and removals immediately; restore the selected files, pause intent and actual download folder from `%LocalAppData%\\fast-torrent-download`.
- Pause and remove with per-torrent progress feedback and a bounded tracker-stop announce wait, while other rows keep updating.
- Show verified bytes and progress for the selected files, fractional KiB/MiB rates and estimated time remaining.
- Inspect per-torrent speed diagnostics: connected/receiving peers, piece coverage, trackers, DHT, effective limits and reported errors.
- Completed downloads automatically leave the queue while files stay on disk. An eight-second notification and the current session's latest 50 activity messages provide access to their folders.
- Configure DHT, peer exchange, local peer discovery, UPnP/NAT-PMP, a specific inbound IPv4 listener, optional required peer encryption and bandwidth caps.
- Choose system, light, or dark theme with a clear selected-theme state.

The current checks include real loopback transfers with a stalled tracker, pause/resume, manual and automatic removal, file integrity and immediate session persistence. The app does not include torrent search, accounts, telemetry, streaming, RSS, scheduling, proxy routing, tracker editing, or remote control.

Listener binding applies to incoming peer/DHT sockets. It is **not** a VPN kill switch or a complete outbound leak-prevention feature: tracker HTTP and other outbound transports are not claimed to be bound.

## Run locally

Install the .NET 10 SDK, then:

```powershell
dotnet build FastTorrentDownload.sln
dotnet run --project src/FastTorrentDownload/FastTorrentDownload.csproj
```

Run the focused checks:

```powershell
dotnet run --project tests/FastTorrentDownload.Tests/FastTorrentDownload.Tests.csproj
```

On an interactive Windows desktop, exercise the native views with an isolated fixture queue (separate from the installed application's data):

```powershell
dotnet run --project tests/FastTorrentDownload.Tests --configuration Release -- --ui-smoke-auto
```

Use `--ui-smoke` instead for manual inspection. Old settings containing a seeding ratio remain readable; the obsolete ratio is ignored because completed downloads are removed automatically.

## Create release artifacts

On Windows with Inno Setup installed, run:

```powershell
.\\scripts\\Publish.ps1 -Version 0.4.0
```

It creates a self-contained Windows x64 portable ZIP, an SHA-256 checksum file, and a per-user installer under `artifacts\\`. The installer is intentionally unsigned: publish the checksum beside it and expect Microsoft SmartScreen to show a reputation warning for early releases.

Pushing a `v*` tag runs the corresponding GitHub Actions release workflow.

## Security

Report vulnerabilities privately through [GitHub Security Advisories](https://github.com/stuckinowhere/fast-torrent-download/security/advisories/new). See [SECURITY.md](SECURITY.md) for supported versions and reporting guidance.
