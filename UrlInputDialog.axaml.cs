using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using System;

namespace AzIPTV;

public partial class UrlInputDialog : Window
{
    public UrlInputDialog()
    {
        InitializeComponent();

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
        var row = new Border
        {
            Margin = new Thickness(0, 1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 2),
            Background = Brushes.Transparent,
        };
        row.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppButtonBorder"));
        row.BorderThickness = new Thickness(1);
        row.DoubleTapped += (_, _) => Close(url);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(170, GridUnitType.Pixel)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        if (!isFixed)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        if (!isFixed)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var nameLabel = new TextBlock
        {
            Text = name,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (isFixed) nameLabel.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppGoldFg"));
        Grid.SetColumn(nameLabel, 0);
        grid.Children.Add(nameLabel);

        var urlLabel = new SelectableTextBlock
        {
            Text = url,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 10, 0),
        };
        if (isFixed) urlLabel.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppGoldFg"));
        ToolTip.SetTip(urlLabel, url);
        Grid.SetColumn(urlLabel, 1);
        grid.Children.Add(urlLabel);

        var loadBtn = MakeActionButton("Load");
        loadBtn.Click += (_, _) => Close(url);
        Grid.SetColumn(loadBtn, 2);
        grid.Children.Add(loadBtn);

        if (!isFixed)
        {
            var editBtn = MakeActionButton("Edit");
            editBtn.Click += async (_, _) => await EditEntryAsync(url, name);
            Grid.SetColumn(editBtn, 3);
            grid.Children.Add(editBtn);

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
            Grid.SetColumn(delBtn, 4);
            grid.Children.Add(delBtn);
        }

        row.Child = grid;
        return row;
    }

    // ── Dialog actions ────────────────────────────────────────────────────────

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)  { Close(null); e.Handled = true; }
    }

    private async void OnAddClicked(object? sender, RoutedEventArgs e)
    {
        var defaultName = $"List #{PlaylistService.LoadUrlHistory().Count + 1}";
        var dialog = new PlaylistEntryDialog("Add IPTV M3U playlist", defaultName, string.Empty);
        var entry = await dialog.ShowDialog<UrlHistoryEntry?>(this);
        if (entry is null) return;

        PlaylistService.SaveUrlHistoryEntry(string.Empty, entry.Url, entry.Name);
        PopulateHistory();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private async System.Threading.Tasks.Task EditEntryAsync(string originalUrl, string name)
    {
        var dialog = new PlaylistEntryDialog("Edit IPTV M3U playlist", name, originalUrl);
        var entry = await dialog.ShowDialog<UrlHistoryEntry?>(this);
        if (entry is null) return;

        PlaylistService.SaveUrlHistoryEntry(originalUrl, entry.Url, entry.Name);
        PopulateHistory();
    }

    private static Button MakeActionButton(string text)
        => new()
        {
            Content = text,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(10, 5),
            MinWidth = 64,
            VerticalAlignment = VerticalAlignment.Center,
        };
}
