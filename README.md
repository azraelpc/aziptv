# AzIPTV

A lightweight Windows IPTV player built with [Avalonia UI](https://avaloniaui.net/) (.NET 8) and [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp).

<img height="250" alt="{B39FA0CB-B849-4441-840A-59CC1E1F2DA0}" src="https://github.com/user-attachments/assets/e42066ad-4125-4410-bd4f-87fd2ee5c2d9" /> <img height="250" alt="{8B29ECDB-8A2E-4E3E-8407-6F8DC7262AE4}" src="https://github.com/user-attachments/assets/1eb68f16-e9ef-4f6c-804c-7b4c2afa3083" />


## Features

- **M3U / M3U8 playlist support** — load from a URL or a local file; duplicate stream URLs can be automatically removed (`RemoveDuplicateChannels=1` in `user.ini`)
- **Channel browser** — side panel with full-text search and group (TV category) filter
- **Keyboard-first navigation** — move through the channel list, search box, and group picker entirely from the keyboard
- **URL history** — save and name your favourite playlists; window title shows the active playlist name
- **Volume control** — mouse wheel, numpad `+`/`-`, or `M` to mute; overlay shows the current level in both windowed and fullscreen modes
- **Always-on-top** — pin the window above other apps
- **Recording (REC)** — press `R` to open the recording scheduler: start immediately or at a scheduled date/time; optional fixed duration (stops automatically); option to close the app when recording ends; pending schedule shown in the button label; a blinking **⬤ REC** overlay shows remaining free disk space on the recording drive
- **Auto-retry / circuit-breaker** — automatically restarts on stream errors, stays alive during active recordings
- **Dark / Light theme** — follows the OS by default, can be overridden and persisted
- **Fullscreen** — press `F` or double-click the video; controls hide automatically
- **Native splash screen** — DPI-aware Win32 splash shown immediately on startup, before the UI loads

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
| `Esc` | Close channel browser / exit fullscreen |
| `P` | Play / Stop |
| `F` | Toggle fullscreen |
| `U` | Load playlist from URL |
| `L` | Load playlist from file |
| `A` | Next aspect ratio |
| `S` | Next audio track |
| `T` | Next subtitle track |
| `R` | Open recording scheduler (start now or scheduled, optional duration + auto-close) |
| `I` | Show stream info overlay (codec, resolution, fps, audio — 10 s, click, or press `I` again to dismiss) |
| `M` | Mute / Unmute |
| `H` | Show keyboard shortcuts help |
| Numpad `+` / `−` | Volume up / down |
| Mouse wheel (over video) | Volume up / down |
| Middle mouse button | Mute / Unmute |
| Double-click | Toggle fullscreen |
| Right-click | Context menu (aspect ratio, audio, subtitles, volume, copy URL, stream info, Football TV Guide, help) |

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

## User Data (`user.ini`)

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
