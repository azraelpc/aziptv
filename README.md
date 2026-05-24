# AzIPTV

A lightweight Windows IPTV player built with [Avalonia UI](https://avaloniaui.net/) (.NET 8) and [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp).

<img height="250" alt="{B39FA0CB-B849-4441-840A-59CC1E1F2DA0}" src="https://github.com/user-attachments/assets/e42066ad-4125-4410-bd4f-87fd2ee5c2d9" /> <img height="250" alt="{8B29ECDB-8A2E-4E3E-8407-6F8DC7262AE4}" src="https://github.com/user-attachments/assets/1eb68f16-e9ef-4f6c-804c-7b4c2afa3083" />


## Features

- **M3U / M3U8 playlist support** — load from a URL or a local file; duplicate stream URLs can be automatically removed (`RemoveDuplicateChannels=1` in `user.ini`)
- **Channel browser** — side panel with full-text search and group (TV category) filter
- **FAVOURITES (MOST VIEWED) group** — channels you have played at least once are automatically ranked by play count at the top of the group list; counts are stored per-playlist in `user.ini`; a **Clear Favourites** entry at the bottom of the list resets all counters (with confirmation)
- **Keyboard-first navigation** — move through the channel list, search box, and group picker entirely from the keyboard
- **URL history** — save and name your favourite playlists; window title shows the active playlist name
- **Volume control** — mouse wheel, numpad `+`/`-`, or `M` to mute; overlay shows the current level in both windowed and fullscreen modes
- **Always-on-top** — pin the window above other apps
- **Recording (REC)** — press `R` to open the recording scheduler: start immediately or at a scheduled date/time; optional fixed duration (stops automatically); option to close the app when recording ends; pending schedule shown in the button label; a blinking **⬤ REC** overlay shows remaining free disk space on the recording drive
- **Auto-retry / circuit-breaker** — automatically restarts on stream errors, stays alive during active recordings
- **Dark / Light theme** — follows the OS by default, can be overridden and persisted
- **Fullscreen** — press `F` or double-click the video; controls hide automatically; `Esc` always exits
- **Aspect ratio cycling** — rotate through standard ratios or reset to default via right-click menu
- **Audio & subtitle track selection** — cycle tracks or disable subtitles from the right-click menu
- **Volume preset submenu** — set volume to 0 / 10 / 25 / 50 / 75 / 100 / 150 / 200 % via right-click
- **Copy stream URL** — copy the current stream URL to clipboard from the right-click menu
- **Stream info overlay** — press `I` to show video/audio codec, resolution, fps, bitrate, and channel info over the video; auto-hides after 10 seconds or dismiss manually
- **Football TV Guide** — right-click context menu opens `https://liveonsat.com/2day.php` in the default browser
- **Keyboard shortcuts help** — press `H` or use the Help button for a full shortcut reference
- **English / Spanish UI** — switch language with the flag button; preference is saved in `user.ini`
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

| Key | Description | Default |
|-----|-------------|---------|
| `Theme` | `Dark` or `Light` | `Dark` |
| `Language` | `en` or `es` | `en` |
| `RemoveDuplicateChannels` | `1` removes duplicate stream URLs on load | `1` |
| `PlaylistUrl` / `PlaylistFile` | Last loaded playlist | — |
| `LastStreamUrl` | Last played stream URL | — |
| `LastGroup` | Last selected TV group in the channel browser | — |
| `RecordingFolder` | Output folder for recordings (set via the REC dialog); defaults to the user's Desktop | — |
| `[Favs_<id>]` sections | Per-playlist play counts (SHA-256 hashed, credential-safe) | — |

Sensitive stream URLs (including any embedded credentials) are **never** stored in plain text — only the hostname is shown in the status bar.

## Dependencies

- [Avalonia](https://avaloniaui.net/) 11.2.7 — cross-platform UI framework
- [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) 3.8.5 — VLC bindings
- [VideoLAN.LibVLC.Windows](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows) 3.0.21 — bundled VLC native libraries

All dependencies are restored automatically via NuGet on first build.

## Todo

- EPG Helper (map Movistar or similar list's channels to m3u list channel and proxy).
- When EPG Helper is ready: show EPG + codec and resolution info in the UI.

---

## Changelog

### 2026-05-24 (later)

- **Stream info overlay (`I`)** — press `I` or right-click → *Stream Info (I)* to display video/audio codec, resolution, fps, bitrate, channel URL, and current volume over the video; auto-hides after 10 seconds, on click, or by pressing `I` again while visible
- **Red recording indicator** — a blinking red **⬤ REC** dot appears in the top-left of the video area (Popup overlay, floats above the VLC native surface) for the entire duration of a recording, including in fullscreen; also shows remaining free disk space on the recording drive (refreshed every 10 s)
- **STOP button countdown** — when recording with a fixed duration, the REC button shows a live countdown (`⏹ STOP  MM:SS (R)` or `H:MM:SS`) updating every second
- **Recording folder in REC dialog** — the *Schedule Recording* dialog now has a **Recording folder** section at the bottom showing the saved path with a **Browse…** button; the chosen folder is saved to `user.ini` immediately when selected (even if the dialog is cancelled); defaults to the user's Desktop on first use; clicking *Schedule start* or *Duration* focuses the corresponding input field automatically; `Esc` acts as Cancel
- **Exit confirmation** — closing the window while a recording is active or scheduled shows a confirmation dialog; three variants depending on whether it's an active recording, a scheduled one, or both; bilingual (EN/ES)
- **Keyboard shortcut hints in context menu** — right-click items now show their shortcut: *Next Aspect Ratio (A)*, *Next Audio Track (S)*, *Next Subtitles Track (T)*, *Mute/Unmute Audio (M)*
- **Volume overlay in windowed mode** — the volume level popup (shown when using `+`/`-` or the mouse wheel) now appears in both windowed and fullscreen modes
- **Football TV Guide** — right-click context menu → *Football TV Guide* opens `https://liveonsat.com/2day.php` in the default browser

### 2026-05-24

- **Recording scheduler** — pressing `R` now opens a dialog instead of immediately starting; choose *Start Now* or a scheduled date/time (`yyyy-MM-dd HH:mm` text input); optional fixed duration (hours + minutes, stops automatically); optional *Close app when done*; pending schedule shown in the REC button label (`⏳ REC HH:mm`); clicking `R` again while scheduled cancels it
- **Last group restored on startup** — the selected TV group in the channel browser is saved to `user.ini` (`LastGroup=`) whenever it changes; restored automatically when the same playlist loads on next launch; falls back to *ALL CHANNELS* if the group no longer exists
- **New keyboard shortcuts** — `A` cycles aspect ratio, `S` cycles audio track, `T` cycles subtitle track; all three added to the help dialog
- **FAVOURITES (MOST VIEWED)** — group renamed; a *✕ Clear Favourites* entry is always shown at the bottom of the list; selecting it prompts for confirmation before clearing all play counts for that playlist from `user.ini`
- **English / Spanish UI** — language toggle button (EN / ES) in the toolbar; all button labels, context menu items, and feedback messages are translated; preference saved to `user.ini` as `Language=en/es`
- **Help dialog** — press `H`, click the Help button, or use the right-click menu to open a keyboard shortcuts reference (language-aware)
- **Right-click context menu expanded**:
  - *Next / Reset Aspect Ratio* — cycles through VLC-supported ratios (1:1, 4:3, 16:9, 16:10, 2.21:1, 2.35:1, 2.39:1, 5:4)
  - *Next Audio Track* — rotates through available audio tracks
  - *Set Volume* submenu — presets: 0 / 10 / 25 / 50 / 75 / 100 / 150 / 200 %
  - *(Un)Mute Audio*
  - *Next Subtitles Track* — enables subtitles if disabled, then cycles tracks
  - *Disable Subtitles*
  - *Help (H)*
  - All actions show feedback in the status bar (windowed) or the top-right overlay (fullscreen)
- **`RemoveDuplicateChannels` setting** — enabled by default; uses a single-pass `HashSet<string>` in the parser for O(n) deduplication; key is auto-written to `user.ini` on first save if absent

### 2026-05-23 (earlier)

- **FAVOURITES group** — channels you have played are automatically ranked by play count; shown at the top of the group selector; per-playlist counts stored in `[Favs_<sha256hash>]` sections in `user.ini` (credential-safe hashing)
- **Copy stream URL** — right-click → *Copy Stream URL* copies the active stream URL to the clipboard
- **Right-click cursor fix** — VLC hid the system cursor over the video; cursor is now restored before the context menu appears
- **Title bar** format: `AzIPTV — Playlist — Channel — W×H@FPS`; updates are debounced (1.5 s) to avoid performance overhead
- **ESC key** exits fullscreen even when no panel is open
- **Channel name on startup** — restored from the loaded playlist when resuming the last stream
- **Recording overwrite fix** — segments are now named `rec_…_001.ts`, `_002.ts`, etc.; existing files are never overwritten on retry
- **Circuit-breaker** skips `Stop()` during an active recording so the recording continues uninterrupted
- `MaxConsecutiveFails` raised from 10 → 1000 (effectively disables the hard stop for stable streams)


## License

MIT — see [LICENSE](LICENSE).
