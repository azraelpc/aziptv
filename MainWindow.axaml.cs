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
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace AzIPTV;

public partial class MainWindow : Window
{
    private string _currentUrl = string.Empty;
    private string _currentChannelName = string.Empty;
    private string _currentListTitle = "AzIPTV";
    private string _currentPlaylistId = string.Empty;
    private System.Collections.Generic.Dictionary<string, int> _playCounts = new();
    private Channel? _pendingFavouriteChannel;

    // Circuit-breaker: pause playback after 10 consecutive failures within 30 s.
    private int      _consecutiveFails;
    private DateTime _firstFailTime = DateTime.MinValue;
    private const int    MaxConsecutiveFails  = 1000;
    private const double FailWindowSeconds    = 30;

    private LibVLC?      _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media?       _media;

    private bool             _isRecording;
    private bool             _confirmingClose;
    private string?          _recordingPath;
    private string           _recordingFolder = string.Empty;
    private DispatcherTimer? _recScheduleTimer;
    private DispatcherTimer? _recDurationTimer;
    private DispatcherTimer? _recCountdownTimer;
    private DateTime         _recEndTime;
    private DispatcherTimer? _recBlinkTimer;
    private DispatcherTimer? _recDiskTimer;
    private DispatcherTimer? _streamInfoTimer;
    private bool             _recCloseWhenDone;

    // Volume state — owned here so rapid adjustments never race with LibVLC's getter.
    private int              _volume  = 100;
    private bool             _isMuted = false;
    private DispatcherTimer? _volumeOverlayTimer;
    private DispatcherTimer? _titleUpdateTimer;

    // VOD seek bar (additive Netflix model)
    private DispatcherTimer? _positionTimer;
    private DispatcherTimer? _seekCommitTimer;
    private long             _seekAccumulatorMs;
    private bool             _seekBarDragging;

    private static readonly string?[] AspectRatios =
        { null, "1:1", "4:3", "16:9", "20:9", "16:10", "2.21:1", "2.35:1", "2.39:1", "5:4" };
    private int _aspectRatioIndex = 0;

    // UI language ("en" or "es") + inline translation helper.
    private string _uiLang = "en";
    private string T(string en, string es) => _uiLang == "es" ? es : en;

    public MainWindow()
    {
        // Read last-played URL synchronously before InitializeComponent so
        // HwndCreated can start playback the moment the VLC surface is ready.
        var (_, _, lastStreamUrl) = PlaylistService.LoadSettings();
        _currentUrl = lastStreamUrl ?? string.Empty;
        var savedFolder = PlaylistService.LoadRecordingFolder();
        _recordingFolder = (!string.IsNullOrEmpty(savedFolder) && Directory.Exists(savedFolder))
            ? savedFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

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
        _mediaPlayer.LengthChanged    += OnVlcLengthChanged;

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
        VideoHost.VideoMouseWheel    += delta => AdjustVolume(delta > 0 ? 5 : -5);
        VideoHost.VideoMiddleClicked += ToggleMute;

        SidePanel.ChannelSelected          += OnChannelSelected;
        SidePanel.PanelCloseRequested      += CloseSidePanel;
        SidePanel.ClearFavouritesRequested += async () => await ClearFavouritesAsync();
        SidePanel.GroupSelectionChanged    += group => PlaylistService.SaveLastGroup(group);

        // Strip WS_EX_TOPMOST from the Popup HWND each time it opens;
        // Avalonia forces all Popups to be system-topmost which we don't want.
        SidePanelPopup.Opened    += OnSidePanelPopupOpened;
        RecIndicatorPopup.Opened += OnRecIndicatorPopupOpened;

        // Wire seek bar pointer events so drag doesn't fight the position timer.
        // Use AddHandler with handledEventsToo:true because Avalonia's Slider marks
        // PointerPressed as Handled internally, which would suppress the plain += subscription.
        SeekBar.AddHandler(InputElement.PointerPressedEvent,
            new EventHandler<PointerPressedEventArgs>((_, _) => _seekBarDragging = true),
            RoutingStrategies.Bubble, handledEventsToo: true);
        SeekBar.AddHandler(InputElement.PointerReleasedEvent,
            new EventHandler<PointerReleasedEventArgs>((_, _) => { _seekBarDragging = false; CommitSeekBarPosition(); }),
            RoutingStrategies.Bubble, handledEventsToo: true);

        Closing += OnClosing;

        // Use tunneling strategy so Tab is intercepted before Avalonia's
        // focus-traversal system can consume it.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        // Defer playlist loading until the window is visible and the visual
        // tree is fully constructed — avoids crashes from UI calls too early.
        Opened += (_, _) =>
        {
            _uiLang = PlaylistService.LoadLanguage();
            ApplySavedTheme();
            ApplyLanguage();
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
        ThemeButton.Content = theme == "Light" ? T("☀ Light", "☀ Claro") : T("🌙 Dark", "🌙 Oscuro");
    }

    private void OnThemeToggled(object? sender, RoutedEventArgs e)
    {
        var current = Application.Current!.RequestedThemeVariant;
        var next = current == ThemeVariant.Light ? "Dark" : "Light";
        SetTheme(next);
        PlaylistService.SaveTheme(next);
    }

    // ── Language ───────────────────────────────────────────────────────────

    private void OnLangClicked(object? sender, RoutedEventArgs e)
    {
        _uiLang = _uiLang == "en" ? "es" : "en";
        PlaylistService.SaveLanguage(_uiLang);
        // Re-render theme button label with new language before full apply.
        var thm = Application.Current!.RequestedThemeVariant == ThemeVariant.Light ? "Light" : "Dark";
        SetTheme(thm);
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(ApplyLanguage); return; }
        try
        {
            ChannelsButton.Content   = T("📺 Channels (Tab)", "📺 Canales (Tab)");
            FullscreenButton.Content = T("□  Fullscreen (F)",  "□  Pantalla (F)");
            LoadUrlButton.Content    = T("🌐 Load URL (U)",   "🌐 URL (U)");
            LoadFileButton.Content   = T("📁 Load File (L)", "📁 Archivo (L)");
            PinButton.Content        = T("📌 On Top",         "📌 Encima");
            HelpButton.Content       = T("❓ Help (H)",        "❓ Ayuda (H)");
            LangButton.Content       = _uiLang == "en" ? "EN" : "ES";
            PlayButton.Content = _mediaPlayer?.IsPlaying == true
                ? T("\u23F8  Pause (P)",  "\u23F8  Pausa (P)")
                : (_mediaPlayer?.State == VLCState.Paused && _mediaPlayer?.IsSeekable == true)
                    ? T("\u25B6  Resume (P)", "\u25B6  Reanudar (P)")
                    : T("\u25B6  Play (P)",   "\u25B6  Play (P)");
            if (_isRecording)
                RecButton.Content = T("⏹ STOP REC (R)", "⏹ STOP REC (R)");
            else if (_recScheduleTimer == null)
                RecButton.Content = T("⏺ REC (R)", "⏺ REC (R)");
            // else: keep the ⏳ scheduled text intact
        }
        catch (Exception ex) { AppLogger.LogException("ApplyLanguage", ex); }
    }

    // ── Help ──────────────────────────────────────────────────────────────

    private void OnHelpClicked(object? sender, RoutedEventArgs e) => ShowHelp();

    private void ShowHelp()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(ShowHelp); return; }
        var dlg = new HelpDialog(_uiLang);
        _ = dlg.ShowDialog(this);
    }

    // ── Startup ──────────────────────────────────────────────────────────────

    private async Task LoadPlaylistOnStartupAsync()
    {
        var (playlistUrl, playlistFile, _) = PlaylistService.LoadSettings();

        if (!string.IsNullOrEmpty(playlistUrl))
        {
            _currentPlaylistId = PlaylistService.GetPlaylistId(playlistUrl);
            _playCounts = PlaylistService.LoadPlayCounts(_currentPlaylistId);
            AppLogger.Log($"Loading playlist from URL…");
            SetLoadingState(true);
            try
            {
                var result = await PlaylistService.LoadFromUrlAsync(playlistUrl);
                AppLogger.Log($"Playlist loaded: {result.Channels.Count} channels");
                AfterPlaylistLoaded(result, playFirst: false, listTitle: ResolveUrlListName(playlistUrl));
            }
            catch (Exception ex) { AppLogger.LogException("startup playlist URL", ex); }
            finally { SetLoadingState(false); }
        }
        else if (!string.IsNullOrEmpty(playlistFile))
        {
            _currentPlaylistId = PlaylistService.GetPlaylistId(playlistFile);
            _playCounts = PlaylistService.LoadPlayCounts(_currentPlaylistId);
            AppLogger.Log($"Loading playlist from file: {System.IO.Path.GetFileName(playlistFile)}");
            SetLoadingState(true);
            try
            {
                var result = await PlaylistService.LoadFromFileAsync(playlistFile);
                AppLogger.Log($"Playlist loaded: {result.Channels.Count} channels");
                AfterPlaylistLoaded(result, playFirst: false, listTitle: Path.GetFileNameWithoutExtension(playlistFile));
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
                LoadUrlButton.Content    = loading ? T("⏳ Loading...", "⏳ Cargando...") : T("🌐 Load URL (U)", "🌐 URL (U)");
                LoadFileButton.Content   = loading ? T("⏳ Loading...", "⏳ Cargando...") : T("📁 Load File (L)", "📁 Archivo (L)");
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
                var actualPath = GetUniqueRecordingPath(_recordingPath);
                var safePath   = actualPath.Replace('\\', '/');
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
                    if (_isRecording)
                    {
                        AppLogger.Log("Too many failures — retrying (recording active)…");
                    }
                    else
                    {
                        AppLogger.Log("Too many failures — playback paused.");
                        _mediaPlayer?.Stop();
                        return;
                    }
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

    private async Task ClearFavouritesAsync()
    {
        bool confirmed = false;
        var dlg = new Window
        {
            Title                 = T("Clear Favourites", "Borrar Favoritos"),
            Width                 = 340,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Padding               = new Thickness(16, 14),
        };
        var yesBtn = new Button { Content = T("Clear", "Borrar"),   HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 4, 0) };
        var noBtn  = new Button { Content = T("Cancel", "Cancelar"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(4, 0, 0, 0) };
        yesBtn.Click += (_, _) => { confirmed = true; dlg.Close(); };
        noBtn.Click  += (_, _) => dlg.Close();
        var btnRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetColumn(yesBtn, 0);
        Grid.SetColumn(noBtn,  1);
        btnRow.Children.Add(yesBtn);
        btnRow.Children.Add(noBtn);
        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = T("Clear the Favourites list?",
                     "\u00bfSeguro que quieres vaciar la lista?"),
        });
        root.Children.Add(btnRow);
        dlg.Content = root;
        await dlg.ShowDialog(this);
        if (!confirmed) return;

        _playCounts.Clear();
        PlaylistService.SavePlayCounts(_currentPlaylistId, _playCounts);
        SidePanel.UpdatePlayCounts(_playCounts);
        ShowActionFeedback(T("Favourites cleared", "Favoritos borrados"));
    }

    private void OnChannelSelected(Channel channel)
    {
        AppLogger.Log($"Channel selected: {channel.Name}");
        _currentChannelName       = channel.Name;
        _pendingFavouriteChannel  = channel;
        UpdateTitle();
        PlayChannelUrl(channel.Url);
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private void OnChannelsClicked(object? sender, RoutedEventArgs e) => ToggleSidePanel();

    private void OnPlayClicked(object? sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is null) return;
        if (_mediaPlayer.IsPlaying)
            _mediaPlayer.Pause(); // freeze on last frame instead of white window
        else if (_mediaPlayer.State == VLCState.Paused && _mediaPlayer.IsSeekable)
            _mediaPlayer.Play();  // resume VOD from paused position
        else
            PlayStream();
    }

    private void OnVlcPlaying(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                PlayButton.Content = T("\u23F8  Pause (P)", "\u23F8  Pausa (P)");
                // Debounce: cancel any pending update before scheduling a new one.
                _titleUpdateTimer?.Stop();
                _titleUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                _titleUpdateTimer.Tick += (_, _) => { _titleUpdateTimer!.Stop(); _titleUpdateTimer = null; UpdateTitle(); };
                _titleUpdateTimer.Start();
                // Increment play count for user-initiated channel selections (live streams only).
                if (_pendingFavouriteChannel is Channel fav && !string.IsNullOrEmpty(_currentPlaylistId)
                    && _mediaPlayer?.IsSeekable != true)
                {
                    _pendingFavouriteChannel = null;
                    var id = PlaylistService.GetChannelId(fav.Url);
                    _playCounts[id] = _playCounts.TryGetValue(id, out var cnt) ? cnt + 1 : 1;
                    PlaylistService.SavePlayCounts(_currentPlaylistId, _playCounts);
                    SidePanel.UpdatePlayCounts(_playCounts);
                }
                else if (_mediaPlayer?.IsSeekable == true)
                {
                    _pendingFavouriteChannel = null; // VOD — don't count
                }
            }
            catch (Exception ex) { AppLogger.LogException("OnVlcPlaying", ex); }
        });

    private void OnVlcStopped(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                PlayButton.Content = T("\u25B6  Play (P)", "\u25B6  Play (P)");
                HideVodBar();
            }
            catch (Exception ex) { AppLogger.LogException("OnVlcStopped", ex); }
        });

    private void OnVlcPaused(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                PlayButton.Content = (_mediaPlayer?.IsSeekable == true)
                    ? T("\u25B6  Resume (P)", "\u25B6  Reanudar (P)")
                    : T("\u25B6  Play (P)",   "\u25B6  Play (P)");
            }
            catch (Exception ex) { AppLogger.LogException("OnVlcPaused", ex); }
        });

    private void OnVlcLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (e.Length > 0 && _mediaPlayer?.IsSeekable == true)
                    ShowVodBar(e.Length);
                else
                    HideVodBar();
            }
            catch (Exception ex) { AppLogger.LogException("OnVlcLengthChanged", ex); }
        });

    // ── VOD seek bar ──────────────────────────────────────────────────────────

    private void ShowVodBar(long totalMs)
    {
        TotalTimeText.Text = FormatTime(totalMs);
        VodBar.IsVisible   = true;
        _positionTimer?.Stop();
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _positionTimer.Tick += (_, _) => UpdateSeekBar();
        _positionTimer.Start();
    }

    private void HideVodBar()
    {
        VodBar.IsVisible = false;
        _positionTimer?.Stop();
        _positionTimer = null;
        _seekCommitTimer?.Stop();
        _seekCommitTimer   = null;
        _seekAccumulatorMs = 0;
        _seekBarDragging   = false;
        CurrentTimeText.Text = "0:00";
        TotalTimeText.Text   = "0:00";
        SeekBar.Value        = 0;
    }

    private void UpdateSeekBar()
    {
        if (_mediaPlayer is null || _seekBarDragging) return;
        var time   = _mediaPlayer.Time;
        var length = _mediaPlayer.Length;
        if (length > 0)
        {
            SeekBar.Value        = (double)time / length;
            CurrentTimeText.Text = FormatTime(time);
        }
    }

    private void CommitSeekBarPosition()
    {
        if (_mediaPlayer is null) return;
        _mediaPlayer.Position = (float)SeekBar.Value;
    }

    private static string FormatTime(long ms)
    {
        if (ms <= 0) return "0:00";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    private void OnFullscreenClicked(object? sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnPinToggled(object? sender, RoutedEventArgs e)
    {
        Topmost = PinButton.IsChecked == true;
        AppLogger.Log(Topmost ? "Pinned — always on top" : "Unpinned");
    }

    private void OnSeekBackClicked(object? sender, RoutedEventArgs e)    => AccumulateSeek(-5_000);
    private void OnSeekForwardClicked(object? sender, RoutedEventArgs e) => AccumulateSeek(5_000);

    private void AccumulateSeek(long deltaMs)
    {
        if (_mediaPlayer is null || !_mediaPlayer.IsSeekable) return;
        _seekAccumulatorMs += deltaMs;
        _seekCommitTimer?.Stop();
        _seekCommitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _seekCommitTimer.Tick += (_, _) =>
        {
            _seekCommitTimer!.Stop();
            _seekCommitTimer = null;
            if (_mediaPlayer is not null)
            {
                var newTime = Math.Clamp(_mediaPlayer.Time + _seekAccumulatorMs, 0, _mediaPlayer.Length);
                _mediaPlayer.Time = newTime;
            }
            _seekAccumulatorMs = 0;
        };
        _seekCommitTimer.Start();
        var absS  = Math.Abs(_seekAccumulatorMs / 1000);
        var label = _seekAccumulatorMs >= 0 ? $"\u23e9 +{absS}s" : $"\u23ea -{absS}s";
        ShowVolumeOverlay(label);
    }

    private async void OnRecClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
        if (_isRecording)            { StopRecording();   return; }
        if (_recScheduleTimer != null) { CancelSchedule(); return; }

        var dlg = new RecordingScheduleDialog(_uiLang, _recordingFolder);
        await dlg.ShowDialog(this);
        if (dlg.Result is null) return;
        if (!string.IsNullOrEmpty(dlg.RecordingFolder) && dlg.RecordingFolder != _recordingFolder)
        {
            _recordingFolder = dlg.RecordingFolder;
            PlaylistService.SaveRecordingFolder(_recordingFolder);
        }

        _recCloseWhenDone = dlg.Result.CloseWhenDone;
        var delay = dlg.Result.StartAt - DateTime.Now;
        if (delay > TimeSpan.FromSeconds(1))
        {
            RecButton.Content = $"\u23F3 REC {dlg.Result.StartAt:HH:mm} (R)";
            ShowActionFeedback(_uiLang == "es"
                ? $"Grabaci\u00f3n programada a las {dlg.Result.StartAt:HH:mm}"
                : $"Recording scheduled for {dlg.Result.StartAt:HH:mm}");
            var scheduledDuration = dlg.Result.Duration;
            _recScheduleTimer = new DispatcherTimer { Interval = delay };
            _recScheduleTimer.Tick += async (_, _) =>
            {
                _recScheduleTimer?.Stop();
                _recScheduleTimer = null;
                await StartRecordingAsync(scheduledDuration);
            };
            _recScheduleTimer.Start();
        }
        else
        {
            await StartRecordingAsync(dlg.Result.Duration);
        }
        }
        catch (Exception ex) { AppLogger.LogException("OnRecClicked", ex); }
    }

    private void CancelSchedule()
    {
        _recScheduleTimer?.Stop();
        _recScheduleTimer = null;
        _recCloseWhenDone = false;
        RecButton.Content = T("\u23FA REC (R)", "\u23FA REC (R)");
        ShowActionFeedback(T("Recording schedule cancelled", "Grabaci\u00f3n cancelada"));
    }

    private async Task StartRecordingAsync(TimeSpan? duration = null)
    {
        if (string.IsNullOrEmpty(_currentUrl))
        {
            AppLogger.Log("No stream is playing — nothing to record.");
            return;
        }

        // Use saved folder silently; prompt only when missing/gone.
        if (string.IsNullOrEmpty(_recordingFolder) || !Directory.Exists(_recordingFolder))
        {
            var top     = TopLevel.GetTopLevel(this)!;
            var folders = await top.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Select Recording Folder" });
            if (folders.Count == 0) return;
            var picked = folders[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(picked)) return;
            _recordingFolder = picked;
            PlaylistService.SaveRecordingFolder(picked);
        }

        var filename   = $"rec_{DateTime.Now:yyyyMMdd_HHmmss}.ts";
        _recordingPath = Path.Combine(_recordingFolder, filename);
        _isRecording   = true;
        RecButton.Content = T("⏹ STOP REC (R)", "⏹ STOP REC (R)");
        AppLogger.Log($"Recording started: {filename}");
        StartRecIndicator();

        if (duration.HasValue)
        {
            AppLogger.Log($"Recording duration: {(int)duration.Value.TotalHours}h {duration.Value.Minutes:D2}min");
            _recDurationTimer = new DispatcherTimer { Interval = duration.Value };
            _recDurationTimer.Tick += (_, _) =>
            {
                _recDurationTimer?.Stop();
                _recDurationTimer = null;
                StopRecording();
                if (_recCloseWhenDone) Close();
            };
            _recDurationTimer.Start();

            _recEndTime = DateTime.Now + duration.Value;
            _recCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _recCountdownTimer.Tick += (_, _) =>
            {
                var remaining = _recEndTime - DateTime.Now;
                if (remaining <= TimeSpan.Zero) { _recCountdownTimer?.Stop(); _recCountdownTimer = null; return; }
                RecButton.Content = remaining.TotalHours >= 1
                    ? $"\u23f9 STOP  {(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2} (R)"
                    : $"\u23f9 STOP  {remaining.Minutes:D2}:{remaining.Seconds:D2} (R)";
            };
            _recCountdownTimer.Start();
        }

        // Restart the stream with the sout recording chain.
        PlayStream();
    }

    private void StartRecIndicator()
    {
        RecDot.Opacity = 1.0;
        UpdateRecDiskSpace();
        RecIndicatorPopup.Open();
        bool dotOn = true;
        _recBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _recBlinkTimer.Tick += (_, _) =>
        {
            dotOn = !dotOn;
            RecDot.Opacity = dotOn ? 1.0 : 0.15;
        };
        _recBlinkTimer.Start();
        // Update disk space every 10 s — DriveInfo is a fast kernel stat, no I/O.
        _recDiskTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _recDiskTimer.Tick += (_, _) => UpdateRecDiskSpace();
        _recDiskTimer.Start();
    }

    private void StopRecIndicator()
    {
        _recBlinkTimer?.Stop();
        _recBlinkTimer = null;
        _recDiskTimer?.Stop();
        _recDiskTimer = null;
        RecDot.Opacity = 1.0;
        RecIndicatorPopup.Close();
    }

    private void UpdateRecDiskSpace()
    {
        try
        {
            var root = Path.GetPathRoot(_recordingFolder);
            if (string.IsNullOrEmpty(root)) return;
            var drive = new System.IO.DriveInfo(root);
            long free = drive.AvailableFreeSpace;
            string label = free >= 1_073_741_824L
                ? $"{free / 1_073_741_824.0:F1} GB free"
                : $"{free / 1_048_576.0:F0} MB free";
            RecDiskText.Text = label;
        }
        catch { RecDiskText.Text = string.Empty; }
    }

    private void StopRecording()
    {
        _recDurationTimer?.Stop();
        _recDurationTimer = null;
        _recCountdownTimer?.Stop();
        _recCountdownTimer = null;
        StopRecIndicator();
        var folder     = Path.GetDirectoryName(_recordingPath);
        _isRecording   = false;
        _recordingPath = null;
        RecButton.Content = T("⏺ REC (R)", "⏺ REC (R)");
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

    private static string GetUniqueRecordingPath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir      = Path.GetDirectoryName(path) ?? ".";
        var nameBase = Path.GetFileNameWithoutExtension(path);
        var ext      = Path.GetExtension(path);
        for (int i = 1; i <= 9999; i++)
        {
            var candidate = Path.Combine(dir, $"{nameBase}_{i:D3}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }

    private void UpdateTitle()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(UpdateTitle); return; }
        var title = _currentListTitle;
        if (!string.IsNullOrEmpty(_currentChannelName))
            title += $" — {_currentChannelName}";
        if (_mediaPlayer is not null)
        {
            uint w = 0, h = 0;
            _mediaPlayer.Size(0, ref w, ref h);
            var fps = _mediaPlayer.Fps;
            if (w > 0 && h > 0)
                title += fps > 0 ? $" — {w}x{h}@{(int)Math.Round(fps)}" : $" — {w}x{h}";
        }
        Title = title;
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

            _currentPlaylistId = PlaylistService.GetPlaylistId(url);
            _playCounts = PlaylistService.LoadPlayCounts(_currentPlaylistId);
            AfterPlaylistLoaded(result, playFirst: true, listTitle: ResolveUrlListName(url));
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
            _currentPlaylistId = PlaylistService.GetPlaylistId(path);
            _playCounts = PlaylistService.LoadPlayCounts(_currentPlaylistId);
            AfterPlaylistLoaded(result, playFirst: true, listTitle: Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex) { AppLogger.LogException("LoadFromFile", ex); }
        finally { SetLoadingState(false); }
    }

    private void AfterPlaylistLoaded(ParseResult result, bool playFirst, string? listTitle = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { AfterPlaylistLoaded(result, playFirst, listTitle); }
                catch (Exception ex) { AppLogger.LogException("AfterPlaylistLoaded", ex); }
            });
            return;
        }
        _currentListTitle   = string.IsNullOrEmpty(listTitle) ? "AzIPTV" : $"AzIPTV — {listTitle}";
        _currentChannelName = string.Empty;

        // On startup (playFirst=false) the last URL is already playing but the
        // channel name was never restored — find it in the freshly loaded list.
        if (!playFirst && !string.IsNullOrEmpty(_currentUrl))
        {
            var match = result.Channels.FirstOrDefault(c =>
                string.Equals(c.Url, _currentUrl, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                _currentChannelName = match.Name;
        }

        UpdateTitle();
        SidePanel.LoadChannels(result, _playCounts);
        SidePanel.TrySelectGroup(PlaylistService.LoadLastGroup());
        OpenSidePanel();
        if (playFirst && result.Channels.Count > 0)
        {
            AppLogger.Log($"Auto-playing first channel: {result.Channels[0].Name}");
            PlayChannelUrl(result.Channels[0].Url);
        }
    }

    /// <summary>Resolves a human-readable name for a playlist URL from history or fixed list.</summary>
    private static string ResolveUrlListName(string url)
    {
        var fix = PlaylistService.FixedPlaylists
            .FirstOrDefault(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));
        if (fix is not null) return fix.Name;
        return PlaylistService.LoadUrlHistory()
            .FirstOrDefault(e => string.Equals(e.Url, url, StringComparison.OrdinalIgnoreCase))?.Name
            ?? (Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url);
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

    private void OnRecIndicatorPopupOpened(object? sender, EventArgs e)
    {
        // Same fix: strip WS_EX_TOPMOST so the REC overlay does not float above
        // other applications when Always-on-Top is not enabled.
        try
        {
            var hwnd = TopLevel.GetTopLevel(RecDot)?.TryGetPlatformHandle()?.Handle;
            if (hwnd.HasValue && hwnd.Value != IntPtr.Zero)
                NativeMethods.SetWindowPos(hwnd.Value, NativeMethods.HWND_NOTOPMOST,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex) { AppLogger.LogException("RecIndicatorPopup.Opened", ex); }
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
                else if (WindowState == WindowState.FullScreen) ToggleFullScreen();
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
            case 0x6B: // VK_ADD (numpad +)
                AdjustVolume(5);
                break;
            case 0x6D: // VK_SUBTRACT (numpad -)
                AdjustVolume(-5);
                break;
            case 0x4D: // VK_M
                if (!panelOpen) ToggleMute();
                break;
            case 0x48: // VK_H
                if (!panelOpen) ShowHelp();
                break;
            case 0x41: // VK_A — aspect ratio
                if (!panelOpen) NextAspectRatio();
                break;
            case 0x53: // VK_S — audio track (sound)
                if (!panelOpen) NextAudioTrack();
                break;
            case 0x54: // VK_T — subtitle track (text)
                if (!panelOpen) NextSubtitleTrack();
                break;
            case 0x25: // VK_LEFT — seek back 5 s (VOD)
                if (!panelOpen) AccumulateSeek(-5_000);
                break;
            case 0x27: // VK_RIGHT — seek forward 5 s (VOD)
                if (!panelOpen) AccumulateSeek(5_000);
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
                else if (WindowState == WindowState.FullScreen) { ToggleFullScreen(); e.Handled = true; }
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
            case Key.Add:      // numpad +
                AdjustVolume(5); e.Handled = true;
                break;
            case Key.Subtract: // numpad -
                AdjustVolume(-5); e.Handled = true;
                break;
            case Key.M:
                if (!panelOpen) { ToggleMute(); e.Handled = true; }
                break;
            case Key.H:
                if (!panelOpen) { ShowHelp(); e.Handled = true; }
                break;
            case Key.A:
                if (!panelOpen) { NextAspectRatio(); e.Handled = true; }
                break;
            case Key.S:
                if (!panelOpen) { NextAudioTrack(); e.Handled = true; }
                break;
            case Key.T:
                if (!panelOpen) { NextSubtitleTrack(); e.Handled = true; }
                break;
            case Key.Left:
                if (!panelOpen) { AccumulateSeek(-5_000); e.Handled = true; }
                break;
            case Key.Right:
                if (!panelOpen) { AccumulateSeek(5_000); e.Handled = true; }
                break;
            case Key.I:
                if (!panelOpen)
                {
                    if (StreamInfoPopup.IsOpen) StopStreamInfo();
                    else ShowStreamInfo();
                    e.Handled = true;
                }
                break;
        }
    }

    // ── Volume ──────────────────────────────────────────────────────────────

    private void AdjustVolume(int delta)
    {
        if (_mediaPlayer is null) return;
        _volume = Math.Clamp(_volume + delta, 0, 200);
        _mediaPlayer.Volume = _volume;
        AppLogger.Log($"Volume: {_volume}%");
        ShowVolumeOverlay($"🔊 {_volume}%");
    }

    private void ToggleMute()
    {
        if (_mediaPlayer is null) return;
        _isMuted = !_isMuted;
        _mediaPlayer.Mute = _isMuted;
        AppLogger.Log(_isMuted ? "Muted" : "Unmuted");
        ShowVolumeOverlay(_isMuted ? "🔇 Muted" : $"🔊 {_volume}%");
    }

    private void ShowVolumeOverlay(string text)
    {
        VolumeOverlayText.Text = text;
        if (!VolumeOverlayPopup.IsOpen)
            VolumeOverlayPopup.Open();

        // Restart the auto-hide timer so rapid changes extend the visibility window.
        _volumeOverlayTimer?.Stop();
        _volumeOverlayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _volumeOverlayTimer.Tick += (_, _) =>
        {
            _volumeOverlayTimer!.Stop();
            _volumeOverlayTimer = null;
            VolumeOverlayPopup.Close();
        };
        _volumeOverlayTimer.Start();
    }

    /// <summary>Logs msg to the status bar and, when fullscreen, also shows it in the overlay popup.</summary>
    private void ShowActionFeedback(string msg)
    {
        AppLogger.Log(msg);
        ShowVolumeOverlay(msg);
    }

    private void ShowStreamInfo()
    {
        if (_mediaPlayer is null) return;
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrEmpty(_currentChannelName))
            sb.AppendLine($"Channel : {_currentChannelName}");
        if (!string.IsNullOrEmpty(_currentUrl))
        {
            var url = _currentUrl.Length > 65 ? _currentUrl[..62] + "..." : _currentUrl;
            sb.AppendLine($"URL     : {url}");
        }

        bool hasVideoTrack = false;
        if (_media is not null)
        {
            foreach (var track in _media.Tracks)
            {
                var codec = FourCcToString(track.Codec);
                if (track.TrackType == TrackType.Video)
                {
                    sb.AppendLine();
                    sb.Append($"Video   : {codec}");
                    if (track.Data.Video.Width > 0)
                        sb.Append($"  {track.Data.Video.Width}\u00d7{track.Data.Video.Height}");
                    if (track.Data.Video.FrameRateDen > 0 && track.Data.Video.FrameRateNum > 0)
                        sb.Append($"  {(double)track.Data.Video.FrameRateNum / track.Data.Video.FrameRateDen:0.##} fps");
                    if (track.Bitrate > 0)
                        sb.Append($"  {track.Bitrate / 1000} kbps");
                    sb.AppendLine();
                    hasVideoTrack = true;
                }
                else if (track.TrackType == TrackType.Audio)
                {
                    sb.Append($"Audio   : {codec}");
                    if (track.Data.Audio.Channels > 0) sb.Append($"  {track.Data.Audio.Channels}ch");
                    if (track.Data.Audio.Rate > 0)     sb.Append($"  {track.Data.Audio.Rate} Hz");
                    if (track.Bitrate > 0)             sb.Append($"  {track.Bitrate / 1000} kbps");
                    if (!string.IsNullOrEmpty(track.Language)) sb.Append($"  [{track.Language}]");
                    sb.AppendLine();
                }
            }
        }

        if (!hasVideoTrack)
        {
            var fps = _mediaPlayer.Fps;
            sb.AppendLine();
            sb.Append("Video   : ");
            sb.AppendLine(fps > 0f ? $"{fps:0.##} fps" : "(info not yet available)");
        }

        sb.AppendLine();
        sb.Append($"Volume  : {_volume}%{(_isMuted ? "  [muted]" : "")}");

        StreamInfoText.Text = sb.ToString().TrimEnd();
        _streamInfoTimer?.Stop();
        StreamInfoPopup.Close();
        StreamInfoPopup.Open();
        _streamInfoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _streamInfoTimer.Tick += (_, _) => StopStreamInfo();
        _streamInfoTimer.Start();
    }

    private void StopStreamInfo()
    {
        _streamInfoTimer?.Stop();
        _streamInfoTimer = null;
        StreamInfoPopup.Close();
    }

    private static string FourCcToString(uint codec)
    {
        if (codec == 0) return "?";
        var c = new char[4];
        c[0] = (char)(codec & 0xFF);
        c[1] = (char)((codec >> 8)  & 0xFF);
        c[2] = (char)((codec >> 16) & 0xFF);
        c[3] = (char)((codec >> 24) & 0xFF);
        return new string(c).Trim('\0').ToUpperInvariant();
    }

    private void OnStreamInfoClick(object? sender, PointerPressedEventArgs e)
    {
        StopStreamInfo();
    }

    private void NextAspectRatio()
    {
        if (_mediaPlayer is null) return;
        _aspectRatioIndex = (_aspectRatioIndex + 1) % AspectRatios.Length;
        var ratio = AspectRatios[_aspectRatioIndex];
        _mediaPlayer.CropGeometry = null;
        _mediaPlayer.AspectRatio  = ratio;
        ShowActionFeedback(T("Aspect ratio: " + (ratio ?? "Default"), "Proporci\u00f3n: " + (ratio ?? "Defecto")));
    }

    private void ResetAspectRatio()
    {
        if (_mediaPlayer is null) return;
        _aspectRatioIndex = 0;
        _mediaPlayer.CropGeometry = null;
        _mediaPlayer.AspectRatio  = null;
        ShowActionFeedback(T("Aspect ratio: Default", "Proporción: Defecto"));
    }

    private void NextAudioTrack()
    {
        if (_mediaPlayer is null) return;
        var valid = _mediaPlayer.AudioTrackDescription.Where(t => t.Id != -1).ToArray();
        if (valid.Length == 0) { ShowActionFeedback(T("No audio tracks available", "Sin pistas de audio")); return; }
        int cur = _mediaPlayer.AudioTrack;
        int idx  = Array.FindIndex(valid, t => t.Id == cur);
        int next = (idx + 1) % valid.Length;
        _mediaPlayer.SetAudioTrack(valid[next].Id);
        ShowActionFeedback(T($"Audio: {valid[next].Name}", $"Audio: {valid[next].Name}"));
    }

    private void SetVolumeTo(int percent)
    {
        if (_mediaPlayer is null) return;
        _volume = Math.Clamp(percent, 0, 200);
        _mediaPlayer.Volume = _volume;
        ShowActionFeedback(T($"Volume: {_volume}%", $"Volumen: {_volume}%"));
    }

    private void NextSubtitleTrack()
    {
        if (_mediaPlayer is null) return;
        var valid = _mediaPlayer.SpuDescription.Where(t => t.Id != -1).ToArray();
        if (valid.Length == 0) { ShowActionFeedback(T("No subtitle tracks available", "Sin subtítulos")); return; }
        int cur = _mediaPlayer.Spu;
        int idx  = Array.FindIndex(valid, t => t.Id == cur);
        // If currently disabled (idx == -1), wrap to 0 (first track).
        int next = (idx + 1) % valid.Length;
        _mediaPlayer.SetSpu(valid[next].Id);
        ShowActionFeedback(T($"Subtitles: {valid[next].Name}", $"Subtítulos: {valid[next].Name}"));
    }

    private void DisableSubtitles()
    {
        if (_mediaPlayer is null) return;
        _mediaPlayer.SetSpu(-1);
        ShowActionFeedback(T("Subtitles disabled", "Subtítulos desactivados"));
    }

    // ── Context menu (right-click on video) ───────────────────────────────────

    private void ShowContextMenu(int screenX, int screenY)
    {
        const uint MF_STRING       = 0x00000000;
        const uint MF_SEPARATOR    = 0x00000800;
        const uint MF_GRAYED       = 0x00000001;
        const uint MF_POPUP        = 0x00000010;
        const uint TPM_RETURNCMD   = 0x0100;
        const uint TPM_RIGHTBUTTON = 0x0002;

        bool hasUrl    = !string.IsNullOrEmpty(_currentUrl);
        bool hasPlayer = _mediaPlayer is not null;

        IntPtr hMenu = NativeMethods.CreatePopupMenu();
        NativeMethods.AppendMenu(hMenu, MF_STRING,    new UIntPtr(1001), "Fullscreen (F)");
        NativeMethods.AppendMenu(hMenu, MF_STRING,    new UIntPtr(1002), "Channel Browser (Tab)");
        NativeMethods.AppendMenu(hMenu, MF_SEPARATOR, new UIntPtr(0),    string.Empty);
        NativeMethods.AppendMenu(hMenu, MF_STRING | (hasUrl ? 0u : MF_GRAYED), new UIntPtr(1003), "Copy Stream URL");
        NativeMethods.AppendMenu(hMenu, MF_STRING | (hasPlayer ? 0u : MF_GRAYED), new UIntPtr(1005), "Stream Info (I)");
        NativeMethods.AppendMenu(hMenu, MF_SEPARATOR, new UIntPtr(0), string.Empty);
        NativeMethods.AppendMenu(hMenu, MF_STRING, new UIntPtr(1007), "Football TV Guide");

        // ── Aspect ratio ─────────────────────────────────────────────────────
        NativeMethods.AppendMenu(hMenu, MF_SEPARATOR, new UIntPtr(0), string.Empty);
        NativeMethods.AppendMenu(hMenu, MF_STRING | (hasPlayer ? 0u : MF_GRAYED), new UIntPtr(2001), "Next Aspect Ratio (A)");
        NativeMethods.AppendMenu(hMenu, MF_STRING | (hasPlayer ? 0u : MF_GRAYED), new UIntPtr(2002), "Reset Aspect Ratio");

        // ── Audio ─────────────────────────────────────────────────────────────
        NativeMethods.AppendMenu(hMenu, MF_SEPARATOR, new UIntPtr(0), string.Empty);
        NativeMethods.AppendMenu(hMenu, MF_STRING | (hasPlayer ? 0u : MF_GRAYED), new UIntPtr(2003), "Next Audio Track (S)");

        // ── Volume submenu ────────────────────────────────────────────────────
        NativeMethods.AppendMenu(hMenu, MF_SEPARATOR, new UIntPtr(0), string.Empty);
        IntPtr hVolMenu = NativeMethods.CreatePopupMenu();
        NativeMethods.AppendMenu(hVolMenu, MF_STRING, new UIntPtr(3001), "0%");
        NativeMethods.AppendMenu(hVolMenu, MF_STRING, new UIntPtr(3002), "10%");
        NativeMethods.AppendMenu(hVolMenu, MF_STRING, new UIntPtr(3003), "25%");
        NativeMethods.AppendMenu(hVolMenu, MF_STRING, new UIntPtr(3004), "50%");
        NativeMethods.AppendMenu(hVolMenu, MF_STRING, new UIntPtr(3005), "75%");
        NativeMethods.AppendMenu(hVolMenu, MF_STRING, new UIntPtr(3006), "100%");
        NativeMethods.AppendMenu(hVolMenu, MF_STRING, new UIntPtr(3007), "150%");
        NativeMethods.AppendMenu(hVolMenu, MF_STRING, new UIntPtr(3008), "200%");
        NativeMethods.AppendMenu(hMenu, MF_POPUP, (UIntPtr)(ulong)hVolMenu.ToInt64(), "Set Volume");
        NativeMethods.AppendMenu(hMenu, MF_STRING, new UIntPtr(2004), _isMuted ? "Unmute Audio (M)" : "Mute Audio (M)");

        // ── Subtitles ─────────────────────────────────────────────────────────
        NativeMethods.AppendMenu(hMenu, MF_SEPARATOR, new UIntPtr(0), string.Empty);
        NativeMethods.AppendMenu(hMenu, MF_STRING | (hasPlayer ? 0u : MF_GRAYED), new UIntPtr(2005), "Next Subtitles Track (T)");
        NativeMethods.AppendMenu(hMenu, MF_STRING | (hasPlayer ? 0u : MF_GRAYED), new UIntPtr(2006), "Disable Subtitles");

        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        // VLC may have hidden the cursor over the video surface — restore it
        // before the menu appears so it doesn't stay invisible during interaction.
        NativeMethods.SetCursor(NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_ARROW));
        VideoHost.SuppressMouseHook = true;
        uint cmd = NativeMethods.TrackPopupMenu(
            hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenX, screenY, 0, hwnd, IntPtr.Zero);
        VideoHost.SuppressMouseHook = false;
        NativeMethods.DestroyMenu(hMenu); // recursively destroys hVolMenu too

        if      (cmd == 1001) ToggleFullScreen();
        else if (cmd == 1002) ToggleSidePanel();
        else if (cmd == 1003 && hasUrl)
        {
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(_currentUrl);
            AppLogger.Log("Stream URL copied to clipboard.");
        }
        else if (cmd == 1005) ShowStreamInfo();
        else if (cmd == 1007) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://liveonsat.com/2day.php") { UseShellExecute = true });
        else if (cmd == 1004) ShowHelp();
        else if (cmd == 2001) NextAspectRatio();
        else if (cmd == 2002) ResetAspectRatio();
        else if (cmd == 2003) NextAudioTrack();
        else if (cmd >= 3001 && cmd <= 3008)
        {
            int[] vols = { 0, 10, 25, 50, 75, 100, 150, 200 };
            SetVolumeTo(vols[cmd - 3001]);
        }
        else if (cmd == 2004) ToggleMute();
        else if (cmd == 2005) NextSubtitleTrack();
        else if (cmd == 2006) DisableSubtitles();
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private async Task ConfirmCloseWithRecordingAsync()
    {
        string msg;
        if (_isRecording && _recScheduleTimer is not null)
            msg = T(
                "A recording is in progress and another is scheduled.\n\nExiting will stop the current recording (the file may be incomplete) and cancel the scheduled one.\n\nExit anyway?",
                "Hay una grabaci\u00f3n en curso y otra programada.\n\nSalir detendr\u00e1 la grabaci\u00f3n actual (el archivo puede quedar incompleto) y cancelar\u00e1 la programada.\n\n\u00bfSalir de todas formas?");
        else if (_isRecording)
            msg = T(
                "Recording in progress. Exiting will stop the recording and the file may be incomplete.\n\nExit anyway?",
                "Grabaci\u00f3n en curso. Salir detendr\u00e1 la grabaci\u00f3n y el archivo puede quedar incompleto.\n\n\u00bfSalir de todas formas?");
        else
            msg = T(
                "A recording is scheduled. Exiting will cancel it.\n\nExit anyway?",
                "Hay una grabaci\u00f3n programada. Salir la cancelar\u00e1.\n\n\u00bfSalir de todas formas?");

        bool confirmed = false;
        var dlg = new Window
        {
            Title                 = T("Exit", "Salir"),
            Width                 = 400,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Padding               = new Thickness(16, 14),
        };
        var yesBtn = new Button { Content = T("Exit", "Salir"),      HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 4, 0) };
        var noBtn  = new Button { Content = T("Cancel", "Cancelar"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(4, 0, 0, 0) };
        yesBtn.Click += (_, _) => { confirmed = true; dlg.Close(); };
        noBtn.Click  += (_, _) => dlg.Close();
        var btnRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetColumn(yesBtn, 0);
        Grid.SetColumn(noBtn,  1);
        btnRow.Children.Add(yesBtn);
        btnRow.Children.Add(noBtn);
        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text         = msg,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        root.Children.Add(btnRow);
        dlg.Content = root;
        await dlg.ShowDialog(this);
        if (!confirmed) return;
        _confirmingClose = true;
        Close();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_confirmingClose && (_isRecording || _recScheduleTimer is not null))
        {
            e.Cancel = true;
            _ = ConfirmCloseWithRecordingAsync();
            return;
        }

        if (_mediaPlayer is not null)
        {
            _mediaPlayer.EndReached      -= OnEndReached;
            _mediaPlayer.EncounteredError -= OnPlaybackError;
            _mediaPlayer.Buffering        -= OnBuffering;
            _mediaPlayer.Playing          -= OnVlcPlaying;
            _mediaPlayer.Stopped          -= OnVlcStopped;
            _mediaPlayer.Paused           -= OnVlcPaused;
            _mediaPlayer.LengthChanged    -= OnVlcLengthChanged;
            _positionTimer?.Stop();
            _seekCommitTimer?.Stop();
            _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
        }
        _media?.Dispose();
        _libVlc?.Dispose();
    }
}
