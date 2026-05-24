using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AzIPTV;

/// <summary>Schedule parameters returned by <see cref="RecordingScheduleDialog"/>.</summary>
public sealed record RecordingSchedule(DateTime StartAt, TimeSpan? Duration, bool CloseWhenDone);

/// <summary>Modal dialog for configuring a recording start time and optional duration.</summary>
public sealed class RecordingScheduleDialog : Window
{
    public RecordingSchedule? Result { get; private set; }
    public string RecordingFolder { get; private set; } = string.Empty;

    private string _selectedFolder;

    private readonly RadioButton   _startNowRadio;
    private readonly RadioButton   _scheduleRadio;
    private readonly TextBox       _dateBox;
    private readonly TextBox       _timeBox;
    private readonly RadioButton   _foreverRadio;
    private readonly RadioButton   _durationRadio;
    private readonly TextBox       _hoursBox;
    private readonly TextBox       _minutesBox;
    private readonly CheckBox      _closeCheck;
    private readonly TextBlock     _folderText;

    public RecordingScheduleDialog(string lang, string recordingFolder)
    {
        bool es = lang == "es";
        _selectedFolder  = recordingFolder;
        Title                 = es ? "Programar Grabación" : "Schedule Recording";
        Width                 = 370;
        SizeToContent         = SizeToContent.Height;
        CanResize             = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Padding               = new Thickness(16, 14);
        this.Bind(BackgroundProperty, new DynamicResourceExtension("AppBarBg"));

        var now = DateTime.Now;

        // ── Start section ─────────────────────────────────────────────────

        _startNowRadio = new RadioButton
        {
            Content   = es ? "Iniciar ahora" : "Start Now",
            GroupName = "StartMode",
            IsChecked = true,
        };
        _scheduleRadio = new RadioButton
        {
            Content   = es ? "Programar inicio" : "Schedule start",
            GroupName = "StartMode",
        };

        _dateBox = new TextBox
        {
            Text        = now.ToString("yyyy-MM-dd"),
            Watermark   = "yyyy-mm-dd",
            IsEnabled   = false,
            Width       = 115,
        };
        _timeBox = new TextBox
        {
            Text        = now.ToString("HH:mm"),
            Watermark   = "hh:mm",
            IsEnabled   = false,
            Width       = 70,
        };

        _scheduleRadio.IsCheckedChanged += (_, _) =>
        {
            bool on = _scheduleRadio.IsChecked == true;
            _dateBox.IsEnabled = on;
            _timeBox.IsEnabled = on;
            if (on) _timeBox.Focus();
        };

        var dateTimeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 6,
            Margin      = new Thickness(24, 6, 0, 0),
        };
        dateTimeRow.Children.Add(_dateBox);
        dateTimeRow.Children.Add(_timeBox);

        var fmtHint = new TextBlock
        {
            Text    = es ? "(aaaa-mm-dd  hh:mm)" : "(yyyy-mm-dd  hh:mm)",
            FontSize = 11,
            Margin  = new Thickness(24, 2, 0, 6),
            Opacity = 0.55,
        };

        var startPanel = new StackPanel { Spacing = 6 };
        startPanel.Children.Add(_startNowRadio);
        startPanel.Children.Add(_scheduleRadio);
        startPanel.Children.Add(dateTimeRow);
        startPanel.Children.Add(fmtHint);

        // ── Separator ─────────────────────────────────────────────────────

        var sep = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 12) };
        sep.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppButtonBorder"));

        // ── Duration section ──────────────────────────────────────────────

        _foreverRadio = new RadioButton
        {
            Content   = es ? "Grabar indefinidamente" : "Record forever",
            GroupName = "DurMode",
            IsChecked = true,
        };
        _durationRadio = new RadioButton
        {
            Content   = es ? "Duración:" : "Duration:",
            GroupName = "DurMode",
        };

        _hoursBox = new TextBox
        {
            Text      = "1",
            Watermark = "0",
            IsEnabled = false,
            Width     = 50,
        };
        _minutesBox = new TextBox
        {
            Text      = "00",
            Watermark = "0",
            IsEnabled = false,
            Width     = 50,
        };

        _closeCheck = new CheckBox
        {
            Content   = es ? "Cerrar app al terminar" : "Close app when done",
            IsEnabled = false,
            Margin    = new Thickness(0, 4, 0, 0),
        };

        _durationRadio.IsCheckedChanged += (_, _) =>
        {
            bool on = _durationRadio.IsChecked == true;
            _hoursBox.IsEnabled   = on;
            _minutesBox.IsEnabled = on;
            _closeCheck.IsEnabled = on;
            if (!on) _closeCheck.IsChecked = false;
            if (on) _hoursBox.Focus();
        };

        var durRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 6,
            Margin      = new Thickness(24, 6, 0, 0),
        };
        durRow.Children.Add(_hoursBox);
        durRow.Children.Add(new TextBlock { Text = "h",   VerticalAlignment = VerticalAlignment.Center });
        durRow.Children.Add(_minutesBox);
        durRow.Children.Add(new TextBlock { Text = "min", VerticalAlignment = VerticalAlignment.Center });

        var durPanel = new StackPanel { Spacing = 6 };
        durPanel.Children.Add(_foreverRadio);
        durPanel.Children.Add(_durationRadio);
        durPanel.Children.Add(durRow);
        durPanel.Children.Add(_closeCheck);

        // ── Folder section ────────────────────────────────────────────────

        var folderSep = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 12) };
        folderSep.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppButtonBorder"));

        var folderLabel = new TextBlock
        {
            Text       = es ? "Carpeta de grabación" : "Recording folder",
            FontWeight = FontWeight.SemiBold,
            Margin     = new Thickness(0, 0, 0, 6),
        };

        _folderText = new TextBlock
        {
            Text         = _selectedFolder,
            FontSize     = 11,
            Opacity      = 0.75,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin       = new Thickness(0, 0, 0, 4),
        };

        var browseBtn = new Button
        {
            Content             = es ? "Examinar…" : "Browse…",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        browseBtn.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this)!;
            var folders = await top.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = es ? "Carpeta de grabación" : "Select Recording Folder" });
            if (folders.Count == 0) return;
            var picked = folders[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(picked))
            {
                _selectedFolder  = picked;
                _folderText.Text = picked;
                PlaylistService.SaveRecordingFolder(picked);
            }
        };

        var folderPanel = new StackPanel { Spacing = 4 };
        folderPanel.Children.Add(_folderText);
        folderPanel.Children.Add(browseBtn);

        // ── Buttons ───────────────────────────────────────────────────────

        var okBtn     = new Button { Content = es ? "Aceptar" : "OK",      HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 4, 0) };
        var cancelBtn = new Button { Content = es ? "Cancelar" : "Cancel", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(4, 0, 0, 0) };
        okBtn.Click     += OnOk;
        cancelBtn.Click += (_, _) => Close();

        var btnRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetColumn(okBtn,     0);
        Grid.SetColumn(cancelBtn, 1);
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);

        // ── Root ──────────────────────────────────────────────────────────

        var startLabel = new TextBlock { Text = es ? "Inicio"    : "Start",    FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        var durLabel   = new TextBlock { Text = es ? "Duración"  : "Duration", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8) };

        var root = new StackPanel();
        root.Children.Add(startLabel);
        root.Children.Add(startPanel);
        root.Children.Add(sep);
        root.Children.Add(durLabel);
        root.Children.Add(durPanel);
        root.Children.Add(folderSep);
        root.Children.Add(folderLabel);
        root.Children.Add(folderPanel);
        root.Children.Add(btnRow);
        Content = root;
    }

    private void OnOk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DateTime startAt;
        if (_startNowRadio.IsChecked == true)
        {
            startAt = DateTime.Now;
        }
        else
        {
            var combined = (_dateBox.Text ?? "").Trim() + " " + (_timeBox.Text ?? "").Trim();
            if (!DateTime.TryParse(combined, out startAt))
                startAt = DateTime.Now;
            if (startAt <= DateTime.Now)
                startAt = DateTime.Now; // past time → start immediately
        }

        TimeSpan? duration = null;
        if (_durationRadio.IsChecked == true)
        {
            int.TryParse((_hoursBox.Text   ?? "").Trim(), out int h);
            int.TryParse((_minutesBox.Text ?? "").Trim(), out int m);
            h = Math.Clamp(h, 0, 99);
            m = Math.Clamp(m, 0, 59);
            var span = TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m);
            if (span > TimeSpan.Zero) duration = span;
        }

        Result = new RecordingSchedule(startAt, duration, _closeCheck.IsChecked == true && duration.HasValue);
        RecordingFolder = string.IsNullOrEmpty(_selectedFolder) ? RecordingFolder : _selectedFolder;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else base.OnKeyDown(e);
    }
}
