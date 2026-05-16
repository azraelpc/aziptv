using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace AzIPTV;

public partial class MainWindow : Window
{
    private string _currentUrl = string.Empty;
    private string _currentChannelName = string.Empty;

    // Circuit-breaker: pause playback after 10 consecutive failures within 30 s.
    private int      _consecutiveFails;
    private DateTime _firstFailTime = DateTime.MinValue;
    private const int    MaxConsecutiveFails  = 10;
    private const double FailWindowSeconds    = 30;

    private LibVLC?      _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media?       _media;

    private bool    _isRecording;
    private string? _recordingPath;

    public MainWindow()
    {
        // Read last-played URL synchronously before InitializeComponent so
        // HwndCreated can start playback the moment the VLC surface is ready.
        var (_, _, lastStreamUrl) = PlaylistService.LoadSettings();
        _currentUrl = lastStreamUrl ?? string.Empty;

        InitializeComponent();

        // Avalonia's Win32 platform is now initialised — safe to use Dispatcher.UIThread.
        AppLogger.MarkUiReady();

        // Wire status bar to logger — logger already marshals to UI thread.
        AppLogger.MessageLogged += msg => StatusText.Text = msg;

        try
        {
            _libVlc = new LibVLC("--http-user-agent=VLC", "--network-caching=2000");
            _mediaPlayer = new MediaPlayer(_libVlc);
        }
        catch (Exception ex)
        {
            AppLogger.LogException("LibVLC init", ex);
            return;
        }

        _mediaPlayer.EndReached      += OnEndReached;
        _mediaPlayer.EncounteredError += OnPlaybackError;
        _mediaPlayer.Buffering        += OnBuffering;
        _mediaPlayer.Playing          += OnVlcPlaying;
        _mediaPlayer.Stopped          += OnVlcStopped;
        _mediaPlayer.Paused           += OnVlcPaused;

        VideoHost.HwndCreated += hwnd =>
        {
            AppLogger.Log("Video surface ready");
            _mediaPlayer.Hwnd = hwnd;
            if (!string.IsNullOrEmpty(_currentUrl))
            {
                AppLogger.Log($"Resuming last stream…");
                PlayStream();
            }
        };

        VideoHost.VideoDoubleTapped   += ToggleFullScreen;
        VideoHost.VideoSingleTapped   += OnVideoSingleTapped;
        VideoHost.VideoContextMenu    += ShowContextMenu;
        VideoHost.VideoKeyPressed     += OnVideoKeyPressed;

        SidePanel.ChannelSelected     += OnChannelSelected;
        SidePanel.PanelCloseRequested += CloseSidePanel;

        // Strip WS_EX_TOPMOST from the Popup HWND each time it opens;
        // Avalonia forces all Popups to be system-topmost which we don't want.
        SidePanelPopup.Opened += OnSidePanelPopupOpened;

        Closing += OnClosing;

        // Use tunneling strategy so Tab is intercepted before Avalonia's
        // focus-traversal system can consume it.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        // Defer playlist loading until the window is visible and the visual
        // tree is fully constructed — avoids crashes from UI calls too early.
        Opened += (_, _) =>
        {
            ApplySavedTheme();
            _ = LoadPlaylistOnStartupAsync();
        };

        // Keep SidePanel height in sync when the window is resized.
        SizeChanged += (_, _) =>
        {
            if (SidePanelPopup.IsOpen && VideoGrid.Bounds.Height > 0)
                SidePanel.Height = VideoGrid.Bounds.Height;
        };
    }

    // ── Theme toggle ──────────────────────────────────────────────────────

    private void ApplySavedTheme()
    {
        var theme = PlaylistService.LoadTheme();
        SetTheme(theme);
    }

    private void SetTheme(string theme)
    {
        Application.Current!.RequestedThemeVariant =
            theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        // Button label always shows the other option (what you'll switch TO).
        ThemeButton.Content = theme == "Light" ? "☀ Light" : "🌙 Dark";
    }

    private void OnThemeToggled(object? sender, RoutedEventArgs e)
    {
        var current = Application.Current!.RequestedThemeVariant;
        var next = current == ThemeVariant.Light ? "Dark" : "Light";
        SetTheme(next);
        PlaylistService.SaveTheme(next);
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    private async Task LoadPlaylistOnStartupAsync()
    {
        var (playlistUrl, playlistFile, _) = PlaylistService.LoadSettings();

        if (!string.IsNullOrEmpty(playlistUrl))
        {
            AppLogger.Log($"Loading playlist from URL…");
            SetLoadingState(true);
            try
            {
                var result = await PlaylistService.LoadFromUrlAsync(playlistUrl);
                AppLogger.Log($"Playlist loaded: {result.Channels.Count} channels");
                AfterPlaylistLoaded(result, playFirst: false);
            }
            catch (Exception ex) { AppLogger.LogException("startup playlist URL", ex); }
            finally { SetLoadingState(false); }
        }
        else if (!string.IsNullOrEmpty(playlistFile))
        {
            AppLogger.Log($"Loading playlist from file: {System.IO.Path.GetFileName(playlistFile)}");
            SetLoadingState(true);
            try
            {
                var result = await PlaylistService.LoadFromFileAsync(playlistFile);
                AppLogger.Log($"Playlist loaded: {result.Channels.Count} channels");
                AfterPlaylistLoaded(result, playFirst: false);
            }
            catch (Exception ex) { AppLogger.LogException("startup playlist file", ex); }
            finally { SetLoadingState(false); }
        }
        else
        {
            AppLogger.Log("No saved playlist. Use \"Load from URL\" or \"Load from File\" to load channels.");
        }
    }

    private void SetLoadingState(bool loading)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                LoadUrlButton.IsEnabled  = !loading;
                LoadFileButton.IsEnabled = !loading;
                LoadUrlButton.Content    = loading ? "⏳ Loading..." : "🌐 Load URL (U)";
                LoadFileButton.Content   = loading ? "⏳ Loading..." : "📁 Load File (L)";
            }
            catch (Exception ex) { AppLogger.LogException("SetLoadingState", ex); }
        });
    }

    // ── Playback ──────────────────────────────────────────────────────────────

    private static string UrlForDisplay(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host;
        return url;
    }

    private void PlayStream()
    {
        if (_libVlc is null || _mediaPlayer is null || string.IsNullOrEmpty(_currentUrl)) return;
        var label = string.IsNullOrEmpty(_currentChannelName) ? UrlForDisplay(_currentUrl) : _currentChannelName;
        AppLogger.Log($"Playing: {label}");
        try
        {
            _media?.Dispose();
            _media = new Media(_libVlc, new Uri(_currentUrl));
            if (_isRecording && !string.IsNullOrEmpty(_recordingPath))
            {
                var safePath = _recordingPath.Replace('\\', '/');
                _media.AddOption($":sout=#duplicate{{dst=display,dst=std{{access=file,mux=ts,dst={safePath}}}}}");
                _media.AddOption(":sout-keep");
            }
            _mediaPlayer.Play(_media);
        }
        catch (Exception ex) { AppLogger.LogException("PlayStream", ex); }
    }

    private void PlayChannelUrl(string url)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { PlayChannelUrl(url); }
                catch (Exception ex) { AppLogger.LogException("PlayChannelUrl", ex); }
            });
            return;
        }
        _currentUrl = url;
        PlaylistService.SaveLastStreamUrl(url);
        // Reset failure counter when the user explicitly changes channel.
        _consecutiveFails = 0;
        PlayStream();
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        // VLC fires this on its own thread — never access UI controls here directly.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                {
                    var label = string.IsNullOrEmpty(_currentChannelName) ? UrlForDisplay(_currentUrl) : _currentChannelName;
                    AppLogger.Log($"Loop: restarting {label}");
                    PlayStream();
                }
            }
            catch (Exception ex) { AppLogger.LogException("OnEndReached", ex); }
        });
    }

    private void OnPlaybackError(object? sender, EventArgs e)
    {
        // Fired on a VLC thread — marshal to UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var label = string.IsNullOrEmpty(_currentChannelName) ? UrlForDisplay(_currentUrl) : _currentChannelName;

                // Update sliding failure window.
                var now = DateTime.UtcNow;
                if ((now - _firstFailTime).TotalSeconds > FailWindowSeconds)
                {
                    _consecutiveFails = 0;
                    _firstFailTime    = now;
                }
                _consecutiveFails++;

                AppLogger.Log($"Playback error ({_consecutiveFails}): {label}");

                if (_consecutiveFails >= MaxConsecutiveFails)
                {
                    _consecutiveFails = 0;
                    _firstFailTime    = DateTime.MinValue;
                    AppLogger.Log($"Too many failures — playback paused.");
                    _mediaPlayer?.Stop();
                    return;
                }

                // Auto-retry after 500 ms.
                {
                    AppLogger.Log($"Retrying in 500 ms…");
                    var retryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    retryTimer.Tick += (_, _) =>
                    {
                        retryTimer.Stop();
                        try { PlayStream(); }
                        catch (Exception ex) { AppLogger.LogException("RetryPlayStream", ex); }
                    };
                    retryTimer.Start();
                }
            }
            catch (Exception ex) { AppLogger.LogException("OnPlaybackError", ex); }
        });
    }

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        // Only log notable buffering events (start=0% and end=100%) to avoid spam.
        if (e.Cache == 0f || e.Cache == 100f)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var label = string.IsNullOrEmpty(_currentChannelName) ? UrlForDisplay(_currentUrl) : _currentChannelName;
                    var state = e.Cache == 0f ? "Buffering…" : "Buffering complete";
                    AppLogger.Log($"{state}: {label}");
                }
                catch (Exception ex) { AppLogger.LogException("OnBuffering", ex); }
            });
        }
    }

    private void OnChannelSelected(Channel channel)
    {
        AppLogger.Log($"Channel selected: {channel.Name}");
        _currentChannelName = channel.Name;
        PlayChannelUrl(channel.Url);
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private void OnChannelsClicked(object? sender, RoutedEventArgs e) => ToggleSidePanel();

    private void OnPlayClicked(object? sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is null) return;
        if (_mediaPlayer.IsPlaying)
            _mediaPlayer.Stop();
        else
            PlayStream();
    }

    private void OnVlcPlaying(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            try { PlayButton.Content = "\u23F9  Stop (P)"; }
            catch (Exception ex) { AppLogger.LogException("OnVlcPlaying", ex); }
        });

    private void OnVlcStopped(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            try { PlayButton.Content = "\u25B6  Play (P)"; }
            catch (Exception ex) { AppLogger.LogException("OnVlcStopped", ex); }
        });

    private void OnVlcPaused(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            try { PlayButton.Content = "\u25B6  Play (P)"; }
            catch (Exception ex) { AppLogger.LogException("OnVlcPaused", ex); }
        });
    private void OnFullscreenClicked(object? sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnPinToggled(object? sender, RoutedEventArgs e)
    {
        Topmost = PinButton.IsChecked == true;
        AppLogger.Log(Topmost ? "Pinned — always on top" : "Unpinned");
    }

    private async void OnRecClicked(object? sender, RoutedEventArgs e)
    {
        if (_isRecording) StopRecording();
        else              await StartRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        if (string.IsNullOrEmpty(_currentUrl))
        {
            AppLogger.Log("No stream is playing — nothing to record.");
            return;
        }

        var top     = TopLevel.GetTopLevel(this)!;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select Recording Folder" });

        if (folders.Count == 0) return;
        var folder = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(folder)) return;

        var filename   = $"rec_{DateTime.Now:yyyyMMdd_HHmmss}.ts";
        _recordingPath = Path.Combine(folder, filename);
        _isRecording   = true;
        RecButton.Content = "⏹ STOP REC (R)";
        AppLogger.Log($"Recording started: {filename}");

        // Restart the stream with the sout recording chain.
        PlayStream();
    }

    private void StopRecording()
    {
        var folder     = Path.GetDirectoryName(_recordingPath);
        _isRecording   = false;
        _recordingPath = null;
        RecButton.Content = "⏺ REC (R)";
        AppLogger.Log("Recording stopped.");

        // Restart without the sout chain.
        PlayStream();

        // Open the recording folder in Explorer.
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception ex) { AppLogger.LogException("OpenRecordingFolder", ex); }
        }
    }

    private async void OnLoadFromUrlClicked(object? sender, RoutedEventArgs e)
    {
        var (savedUrl, _, _) = PlaylistService.LoadSettings();
        var dialog = new UrlInputDialog(savedUrl);
        var url = await dialog.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(url)) return;

        AppLogger.Log($"Downloading playlist from URL…");
        SetLoadingState(true);
        try
        {
            var result = await PlaylistService.LoadFromUrlAsync(url);
            PlaylistService.SaveSettings(url, null);
            AppLogger.Log($"Playlist loaded: {result.Channels.Count} channels");

            // Ask for a name only for user-entered URLs (not fixed playlists).
            bool isFixed = PlaylistService.FixedPlaylists
                .Any(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));
            if (!isFixed)
            {
                var existing = PlaylistService.LoadUrlHistory()
                    .FirstOrDefault(e2 => string.Equals(e2.Url, url, StringComparison.OrdinalIgnoreCase));
                var suggested = existing?.Name
                    ?? $"List #{PlaylistService.LoadUrlHistory().Count + 1}";
                var nameDialog = new PlaylistNameDialog(suggested);
                var name = await nameDialog.ShowDialog<string?>(this);
                // AddUrlToHistory deduplicates; UpdateUrlName persists the chosen name.
                PlaylistService.AddUrlToHistory(url);
                if (!string.IsNullOrWhiteSpace(name))
                    PlaylistService.UpdateUrlName(url, name);
            }

            AfterPlaylistLoaded(result, playFirst: true);
        }
        catch (Exception ex) { AppLogger.LogException("LoadFromUrl", ex); }
        finally { SetLoadingState(false); }
    }

    private async void OnLoadFromFileClicked(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this)!;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Select M3U Playlist",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("M3U Playlist") { Patterns = new[] { "*.m3u", "*.m3u8" } },
                new FilePickerFileType("All Files")    { Patterns = new[] { "*.*"             } }
            }
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        AppLogger.Log($"Loading playlist from file: {System.IO.Path.GetFileName(path)}…");
        SetLoadingState(true);
        try
        {
            var result = await PlaylistService.LoadFromFileAsync(path);
            PlaylistService.SaveSettings(null, path);
            AppLogger.Log($"Playlist loaded: {result.Channels.Count} channels");
            AfterPlaylistLoaded(result, playFirst: true);
        }
        catch (Exception ex) { AppLogger.LogException("LoadFromFile", ex); }
        finally { SetLoadingState(false); }
    }

    private void AfterPlaylistLoaded(ParseResult result, bool playFirst)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { AfterPlaylistLoaded(result, playFirst); }
                catch (Exception ex) { AppLogger.LogException("AfterPlaylistLoaded", ex); }
            });
            return;
        }
        SidePanel.LoadChannels(result);
        OpenSidePanel();
        if (playFirst && result.Channels.Count > 0)
        {
            AppLogger.Log($"Auto-playing first channel: {result.Channels[0].Name}");
            PlayChannelUrl(result.Channels[0].Url);
        }
    }

    // ── Side panel ────────────────────────────────────────────────────────────
    private void OnSidePanelPopupOpened(object? sender, EventArgs e)
    {
        // Avalonia unconditionally creates Popup HWNDs with WS_EX_TOPMOST, making
        // them float above every other application.  Remove that flag immediately
        // after the native window is created so the panel stays above our own
        // video surface (because it's a separate HWND) but not above other apps.
        try
        {
            var hwnd = TopLevel.GetTopLevel(SidePanel)?.TryGetPlatformHandle()?.Handle;
            if (hwnd.HasValue && hwnd.Value != IntPtr.Zero)
                NativeMethods.SetWindowPos(hwnd.Value, NativeMethods.HWND_NOTOPMOST,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex) { AppLogger.LogException("SidePanelPopup.Opened", ex); }
    }
    private void OnVideoSingleTapped()
    {
        // Close the panel on a click outside it; don't reopen it.
        if (SidePanelPopup.IsOpen) CloseSidePanel();
    }

    private void ToggleSidePanel()
    {
        if (SidePanelPopup.IsOpen) CloseSidePanel();
        else                       OpenSidePanel();
    }

    private void OpenSidePanel()
    {
        if (VideoGrid.Bounds.Height > 0)
            SidePanel.Height = VideoGrid.Bounds.Height;
        SidePanelPopup.Open();
        SidePanel.FocusSearch();
    }

    private void CloseSidePanel() => SidePanelPopup.Close();

    // ── Fullscreen toggle ─────────────────────────────────────────────────────

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = WindowState.Normal;
            ControlsBar.IsVisible = true;
            StatusBar.IsVisible   = true;
        }
        else
        {
            WindowState = WindowState.FullScreen;
            ControlsBar.IsVisible = false;
            StatusBar.IsVisible   = false;
        }
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    // Called by the WH_KEYBOARD_LL hook in VideoView when VLC HWNDs have focus.
    private void OnVideoKeyPressed(uint vkCode)
    {
        bool panelOpen = SidePanelPopup.IsOpen;
        switch (vkCode)
        {
            case 0x09: // VK_TAB
                ToggleSidePanel();
                break;
            case 0x1B: // VK_ESCAPE
                if (panelOpen) CloseSidePanel();
                break;
            case 0x46: // VK_F
                if (!panelOpen) ToggleFullScreen();
                break;
            case 0x50: // VK_P
                if (!panelOpen) OnPlayClicked(null, null!);
                break;
            case 0x55: // VK_U
                if (!panelOpen) OnLoadFromUrlClicked(null, null!);
                break;
            case 0x4C: // VK_L
                if (!panelOpen) OnLoadFromFileClicked(null, null!);
                break;
            case 0x52: // VK_R
                if (!panelOpen) OnRecClicked(null, null!);
                break;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool panelOpen = SidePanelPopup.IsOpen;

        switch (e.Key)
        {
            case Key.Tab:
                ToggleSidePanel();
                e.Handled = true;  // prevent focus traversal
                break;
            case Key.Escape:
                if (panelOpen) { CloseSidePanel(); e.Handled = true; }
                break;
            case Key.F:
                if (!panelOpen) { ToggleFullScreen(); e.Handled = true; }
                break;
            case Key.P:
                if (!panelOpen) { OnPlayClicked(null, null!); e.Handled = true; }
                break;
            case Key.U:
                if (!panelOpen) { OnLoadFromUrlClicked(null, null!); e.Handled = true; }
                break;
            case Key.L:
                if (!panelOpen) { OnLoadFromFileClicked(null, null!); e.Handled = true; }
                break;
            case Key.R:
                if (!panelOpen) { OnRecClicked(null, null!); e.Handled = true; }
                break;
        }
    }

    // ── Context menu (right-click on video) ───────────────────────────────────

    private void ShowContextMenu(int screenX, int screenY)
    {
        const uint MF_STRING       = 0x00000000;
        const uint TPM_RETURNCMD   = 0x0100;
        const uint TPM_RIGHTBUTTON = 0x0002;

        IntPtr hMenu = NativeMethods.CreatePopupMenu();
        NativeMethods.AppendMenu(hMenu, MF_STRING, new UIntPtr(1001), "Fullscreen (F)");
        NativeMethods.AppendMenu(hMenu, MF_STRING, new UIntPtr(1002), "Channel Browser (Tab)");

        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        VideoHost.SuppressMouseHook = true;
        uint cmd = NativeMethods.TrackPopupMenu(
            hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenX, screenY, 0, hwnd, IntPtr.Zero);
        VideoHost.SuppressMouseHook = false;
        NativeMethods.DestroyMenu(hMenu);

        if      (cmd == 1001) ToggleFullScreen();
        else if (cmd == 1002) ToggleSidePanel();
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.EndReached      -= OnEndReached;
            _mediaPlayer.EncounteredError -= OnPlaybackError;
            _mediaPlayer.Buffering        -= OnBuffering;
            _mediaPlayer.Playing          -= OnVlcPlaying;
            _mediaPlayer.Stopped          -= OnVlcStopped;
            _mediaPlayer.Paused           -= OnVlcPaused;
            _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
        }
        _media?.Dispose();
        _libVlc?.Dispose();
    }
}
