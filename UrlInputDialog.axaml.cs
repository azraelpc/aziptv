using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace AzIPTV;

public partial class UrlInputDialog : Window
{
    // Parameterless ctor required by the Avalonia XAML resource loader.
    public UrlInputDialog() : this(null) { }

    public UrlInputDialog(string? prefill = null)
    {
        InitializeComponent();

        if (!string.IsNullOrEmpty(prefill))
            UrlBox.Text = prefill;

        PopulateHistory();

        KeyDown += OnDialogKeyDown;
    }

    // ── History list ──────────────────────────────────────────────────────────

    private void PopulateHistory()
    {
        HistoryPanel.Children.Clear();
        foreach (var entry in PlaylistService.FixedPlaylists)
            HistoryPanel.Children.Add(MakeRow(entry.Url, entry.Name, isFixed: true));
        foreach (var entry in PlaylistService.LoadUrlHistory())
            HistoryPanel.Children.Add(MakeRow(entry.Url, entry.Name, isFixed: false));
    }

    private Control MakeRow(string url, string name, bool isFixed)
    {
        var row = new Grid { Margin = new Thickness(0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(120, GridUnitType.Pixel))); // name
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));                          // url
        if (!isFixed)
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));                     // delete

        // ── Col 0: Name (read-only label for both fixed and user entries) ─────
        var nameLabel = new TextBlock
        {
            Text = name,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (isFixed) nameLabel.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppGoldFg"));
        Grid.SetColumn(nameLabel, 0);
        row.Children.Add(nameLabel);

        // ── Col 1: URL button ─────────────────────────────────────────────────
        var urlLabel = new TextBlock
        {
            Text = url,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (isFixed) urlLabel.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppGoldFg"));
        var urlBtn = new Button
        {
            Content = urlLabel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 5),
        };
        ToolTip.SetTip(urlBtn, url);
        urlBtn.Click += (_, _) => UrlBox.Text = url;
        Grid.SetColumn(urlBtn, 1);
        row.Children.Add(urlBtn);

        // ── Col 2: Delete button (user entries only) ──────────────────────────
        if (!isFixed)
        {
            var delBtn = new Button
            {
                Content = "✕",
                Foreground = Brushes.Red,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 5),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(delBtn, "Remove from history");
            delBtn.Click += (_, _) =>
            {
                PlaylistService.RemoveUrlFromHistory(url);
                PopulateHistory();
            };
            Grid.SetColumn(delBtn, 2);
            row.Children.Add(delBtn);
        }

        return row;
    }

    // ── Dialog actions ────────────────────────────────────────────────────────

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)        { Confirm();     e.Handled = true; }
        else if (e.Key == Key.Escape)  { Close(null);   e.Handled = true; }
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)     => Confirm();
    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void Confirm()
    {
        var url = UrlBox.Text?.Trim();
        Close(string.IsNullOrEmpty(url) ? null : url);
    }
}
