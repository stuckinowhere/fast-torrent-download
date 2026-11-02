# fast torrent download

[![CI](https://github.com/stuckinowhere/fast-torrent-download/actions/workflows/ci.yml/badge.svg)](https://github.com/stuckinowhere/fast-torrent-download/actions/workflows/ci.yml)
[![CodeQL](https://github.com/stuckinowhere/fast-torrent-download/actions/workflows/codeql.yml/badge.svg)](https://github.com/stuckinowhere/fast-torrent-download/actions/workflows/codeql.yml)
[![Release](https://img.shields.io/github/v/release/stuckinowhere/fast-torrent-download)](https://github.com/stuckinowhere/fast-torrent-download/releases/latest)

A local-first Windows 11 x64 BitTorrent client built with Avalonia and MonoTorrent. Add magnet links or `.torrent` files, choose exactly which files to download, and manage the queue without accounts, ads, or telemetry. Use it only for content you have the right to share or download.

## What works now

- Add a user-provided magnet link or `.torrent` file to a chosen download folder. The installer can register the app for `magnet:` links and `.torrent` files; a second launch hands its link to the running window.
- Select or deselect individual files before starting a torrent, and change that selection later from the row's **Files…** button. **Move…** relocates a torrent's files to another folder.
- Every row carries its own Resume/Pause, Recheck, Files, Move, Show folder and Remove actions; removing keeps downloaded files.
- Optional "maximum active downloads": extra torrents wait as *Queued* and start automatically when a slot frees up. Duplicate adds are rejected with a clear message.
- Persist confirmed additions and removals immediately; restore the selected files, pause intent and actual download folder from `%LocalAppData%\\fast-torrent-download`.
- Pause and remove with per-torrent progress feedback and a bounded tracker-stop announce wait, while other rows keep updating.
- Show verified bytes and progress for the selected files, fractional KiB/MiB rates and estimated time remaining.
- Inspect per-torrent speed diagnostics (refreshed every three seconds): connected/receiving peers, piece coverage, trackers, DHT, effective limits and reported errors.
- Completed downloads automatically leave the queue while files stay on disk. An eight-second notification and the current session's latest 50 activity messages provide access to their folders.
- Configure DHT, peer exchange, local peer discovery, UPnP/NAT-PMP, a specific inbound IPv4 listener, optional required peer encryption and bandwidth caps.
- Close to tray (optional), start with Windows minimized to the tray (optional), and system/light/dark theme.

The current checks include real loopback transfers with a stalled tracker, pause/resume, manual and automatic removal, file integrity, immediate session persistence, file reselection, the active-download queue, moves and second-instance forwarding. The app does not include torrent search, accounts, telemetry, streaming, RSS, scheduling, proxy routing, tracker editing, or remote control.

## Privacy

Magnet file lists come from the swarm (DHT, peer exchange, trackers). The optional **Fetch magnet file lists from public .torrent caches** setting is off by default: when enabled, the info-hash of each magnet you add is sent to itorrents.org, torrage.info and btcache.me to resolve the file list faster; only the info-hash-verified file list is used from a cache response, never its tracker or web-seed URLs. The update check contacts `api.github.com` only.

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
.\\scripts\\Publish.ps1 -Version 0.6.0
```

It creates a self-contained Windows x64 portable ZIP, an SHA-256 checksum file, and a per-user installer under `artifacts\\`. The installer is intentionally unsigned: publish the checksum beside it and expect Microsoft SmartScreen to show a reputation warning for early releases. Only the installer registers the `magnet:` protocol and `.torrent` association (both optional tasks, per-user, removed on uninstall); the portable ZIP does not touch the registry.

Pushing a `v*` tag runs the corresponding GitHub Actions release workflow.

## Security

Report vulnerabilities privately through [GitHub Security Advisories](https://github.com/stuckinowhere/fast-torrent-download/security/advisories/new). See [SECURITY.md](SECURITY.md) for supported versions and reporting guidance.
