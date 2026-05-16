using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace AzIPTV;

/// <summary>
/// Small modal dialog that asks the user to name a newly-added playlist URL.
/// Returns the entered name, or null if the user cancels.
/// </summary>
public sealed class PlaylistNameDialog : Window
{
    private readonly TextBox _nameBox;

    public PlaylistNameDialog(string suggestedName)
    {
        Title                 = "Name this Playlist";
        Width                 = 380;
        SizeToContent         = SizeToContent.Height;
        CanResize             = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Padding               = new Thickness(16, 14);
        this.Bind(BackgroundProperty, new DynamicResourceExtension("AppBarBg"));

        _nameBox = new TextBox
        {
            Text            = suggestedName,
            BorderThickness = new Thickness(1),
            Margin          = new Thickness(0, 0, 0, 12),
        };
        _nameBox.Bind(TextBox.BackgroundProperty,  new DynamicResourceExtension("AppInputBg"));
        _nameBox.Bind(TextBox.ForegroundProperty,  new DynamicResourceExtension("AppButtonFg"));
        _nameBox.Bind(TextBox.CaretBrushProperty,  new DynamicResourceExtension("AppCaretBg"));
        _nameBox.Bind(TextBox.BorderBrushProperty, new DynamicResourceExtension("AppButtonBorder"));

        var okBtn = MakeButton("OK");
        okBtn.Click += (_, _) => Confirm();

        var cancelBtn = MakeButton("Cancel");
        cancelBtn.Click += (_, _) => Close(null);

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
        };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);

        var panel = new StackPanel { Spacing = 0 };
        var promptLabel = new TextBlock
        {
            Text   = "Enter a name for this playlist:",
            Margin = new Thickness(0, 0, 0, 8),
        };
        promptLabel.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppButtonFg"));
        panel.Children.Add(promptLabel);
        panel.Children.Add(_nameBox);
        panel.Children.Add(buttons);

        Content = panel;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)  { Confirm();   e.Handled = true; }
            if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
        };

        // Pre-select the text so the user can type a name right away.
        Opened += (_, _) => { _nameBox.Focus(); _nameBox.SelectAll(); };
    }

    private static Button MakeButton(string label)
    {
        var btn = new Button
        {
            Content                    = label,
            Width                      = 80,
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
        Close(string.IsNullOrEmpty(name) ? null : name);
    }
}
