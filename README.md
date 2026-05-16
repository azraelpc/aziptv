# AzIPTV

A lightweight Windows IPTV player built with [Avalonia UI](https://avaloniaui.net/) (.NET 8) and [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp).

## Features

- **M3U / M3U8 playlist support** — load from a URL or a local file
- **Channel browser** — side panel with full-text search and group filter
- **URL history** — save and name your favourite playlists
- **Always-on-top** — pin the window above other apps
- **Recording (REC)** — record the current stream to a `.ts` file
- **Auto-retry / circuit-breaker** — automatically restarts on stream errors, pauses after too many consecutive failures
- **Dark / Light theme** — follows the OS by default, can be overridden and persisted
- **Fullscreen** — press F or double-click the video; controls hide automatically
- **Keyboard shortcuts** — full keyboard control even when the VLC surface has focus

## Requirements

| Component | Version |
|-----------|---------|
| .NET      | 8.0+    |
| Windows   | 10 / 11 (x64) |

> Linux / macOS are not supported — the app uses Win32 P/Invoke for the VLC overlay, mouse/keyboard hooks, and window management.

## Build

```bash
git clone https://github.com/<your-username>/AzIPTV.git
cd AzIPTV
dotnet build -c Release
```

Or run directly:

```bash
dotnet run
```

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `Tab` | Toggle channel browser |
| `Esc` | Close channel browser |
| `P` | Play / Stop |
| `F` | Toggle fullscreen |
| `U` | Load playlist from URL |
| `L` | Load playlist from file |
| `R` | Start / Stop recording |

## User Data

Settings and URL history are saved to `user.ini` next to the executable.  
This file is excluded from version control (see [`.gitignore`](.gitignore)).

Sensitive stream URLs (including any embedded credentials) are stored **base64-encoded** in `user.ini` and are **never** shown in full in the status bar — only the hostname is displayed.

## Dependencies

- [Avalonia](https://avaloniaui.net/) 11.2.7 — cross-platform UI framework
- [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) 3.8.5 — VLC bindings
- [VideoLAN.LibVLC.Windows](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows) 3.0.21 — bundled VLC native libraries

All dependencies are restored automatically via NuGet on first build.

## License

MIT — see [LICENSE](LICENSE).
