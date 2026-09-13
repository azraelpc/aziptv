using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace AzIPTV;

public sealed class PlaylistEntryDialog : Window
{
    private readonly TextBox _nameBox;
    private readonly TextBox _urlBox;

    public PlaylistEntryDialog(string title, string initialName, string initialUrl)
    {
        Title                 = title;
        Width                 = 520;
        SizeToContent         = SizeToContent.Height;
        CanResize             = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Padding               = new Thickness(16, 14);
        this.Bind(BackgroundProperty, new DynamicResourceExtension("AppBarBg"));

        _nameBox = MakeTextBox(initialName);
        _urlBox = MakeTextBox(initialUrl);

        var nameLabel = MakeLabel("Playlist Name");
        var urlLabel = MakeLabel("Playlist URL");

        var saveBtn = MakeButton("Save");
        saveBtn.Click += (_, _) => Confirm();

        var cancelBtn = MakeButton("Cancel");
        cancelBtn.Click += (_, _) => Close(null);

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Margin              = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(saveBtn);
        buttons.Children.Add(cancelBtn);

        Content = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                nameLabel,
                _nameBox,
                urlLabel,
                _urlBox,
                buttons,
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)  { Confirm();   e.Handled = true; }
            if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
        };

        Opened += (_, _) =>
        {
            _nameBox.Focus();
            _nameBox.SelectAll();
        };
    }

    private TextBox MakeTextBox(string initialText)
    {
        var textBox = new TextBox
        {
            Text            = initialText,
            BorderThickness = new Thickness(1),
            Margin          = new Thickness(0, 0, 0, 12),
        };
        textBox.Bind(TextBox.BackgroundProperty,  new DynamicResourceExtension("AppInputBg"));
        textBox.Bind(TextBox.ForegroundProperty,  new DynamicResourceExtension("AppButtonFg"));
        textBox.Bind(TextBox.CaretBrushProperty,  new DynamicResourceExtension("AppCaretBg"));
        textBox.Bind(TextBox.BorderBrushProperty, new DynamicResourceExtension("AppButtonBorder"));
        return textBox;
    }

    private static TextBlock MakeLabel(string text)
    {
        var label = new TextBlock
        {
            Text   = text,
            Margin = new Thickness(0, 0, 0, 8),
        };
        label.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppButtonFg"));
        return label;
    }

    private static Button MakeButton(string label)
    {
        var btn = new Button
        {
            Content                    = label,
            Width                      = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            BorderThickness            = new Thickness(1),
            CornerRadius               = new CornerRadius(4),
            Padding                    = new Thickness(8, 5),
        };
        btn.Bind(Button.BackgroundProperty,  new DynamicResourceExtension("AppButtonBg"));
        btn.Bind(Button.ForegroundProperty,  new DynamicResourceExtension("AppButtonFg"));
        btn.Bind(Button.BorderBrushProperty, new DynamicResourceExtension("AppButtonBorder"));
        return btn;
    }

    private void Confirm()
    {
        var name = _nameBox.Text?.Trim();
        var url = _urlBox.Text?.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
            return;

        Close(new UrlHistoryEntry(url, name));
    }
}