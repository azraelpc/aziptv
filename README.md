# AzIPTV

A lightweight Windows IPTV player built with [Avalonia UI](https://avaloniaui.net/) (.NET 8) and [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp).

## Features

- **M3U / M3U8 playlist support** — load from a URL or a local file
- **Channel browser** — side panel with full-text search and group (TV category) filter
- **Keyboard-first navigation** — move through the channel list, search box, and groups entirely from the keyboard
- **URL history** — save and name your favourite playlists; window title shows the active playlist name
- **Volume control** — mouse wheel, numpad `+`/`-`, or `M` to mute; fullscreen overlay shows the current level
- **Always-on-top** — pin the window above other apps
- **Recording (REC)** — record the current stream to a `.ts` file
- **Auto-retry / circuit-breaker** — automatically restarts on stream errors, pauses after too many consecutive failures
- **Dark / Light theme** — follows the OS by default, can be overridden and persisted
- **Fullscreen** — press `F` or double-click the video; controls hide automatically
- **Native splash screen** — DPI-aware Win32 splash shown immediately on startup, before the UI loads (first run is always slower)

## Requirements

| Component | Version |
|-----------|---------|
| .NET      | 8.0+    |
| Windows   | 10 / 11 (x64) |

> Linux / macOS are not supported — the app uses Win32 P/Invoke for the VLC overlay, mouse/keyboard hooks, native splash, and window management.

## Build

```bash
git clone https://github.com/azraelpc/aziptv.git
cd AzIPTV
dotnet build -c Release
```

Or run directly:

```bash
dotnet run
```

## Keyboard Shortcuts

### Global (video has focus or panel is closed)

| Key | Action |
|-----|--------|
| `Tab` | Toggle channel browser |
| `Esc` | Close channel browser |
| `P` | Play / Stop |
| `F` | Toggle fullscreen |
| `U` | Load playlist from URL |
| `L` | Load playlist from file |
| `R` | Start / Stop recording |
| `M` | Mute / Unmute |
| Numpad `+` / `−` | Volume up / down |
| Mouse wheel (over video) | Volume up / down |
| Middle mouse button | Mute / Unmute |

### Channel browser (side panel open)

| Key | Action |
|-----|--------|
| `↓` / `↑` | Move selection in channel list |
| `Page Down` / `Page Up` | Jump 10 channels |
| `Enter` | Play selected channel |
| `↑` at top of list | Open group (TV category) picker |
| `↓` / `↑` in group picker | Navigate groups |
| `Enter` in group picker | Confirm group, filter list, return focus |
| `Esc` in group picker | Cancel, return to search |
| Type anything | Focus search box and filter |

## User Data

Settings and URL history are saved to `user.ini` next to the executable.  
This file is excluded from version control (see [`.gitignore`](.gitignore)).

Sensitive stream URLs (including any embedded credentials) are stored/encoded in `user.ini` and **never** shown in full in the status bar — only the hostname is displayed.

## Dependencies

- [Avalonia](https://avaloniaui.net/) 11.2.7 — cross-platform UI framework
- [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) 3.8.5 — VLC bindings
- [VideoLAN.LibVLC.Windows](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows) 3.0.21 — bundled VLC native libraries

All dependencies are restored automatically via NuGet on first build.

## Todo

- Favourites (most played channels - per list+group?).
- Aspect ratio / Subtitles / Audio track keys.
- Copy current channel url to clipboard.
- EPG Helper (map Movistar or similar list's channels to m3u list channel (and proxy?).
- When EPG Helper is ok, show EPG + codec and resolution info.
- Time scheduler for recording (and maybe for poweroff).


## License

MIT — see [LICENSE](LICENSE).
