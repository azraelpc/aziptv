using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace AzIPTV;

/// <summary>
/// Read-only modal dialog showing all keyboard shortcuts in the current UI language.
/// </summary>
public sealed class HelpDialog : Window
{
    public HelpDialog(string lang)
    {
        bool es = lang == "es";
        Title                 = es ? "AzIPTV \u2014 Atajos de Teclado" : "AzIPTV \u2014 Keyboard Shortcuts";
        Width                 = 490;
        SizeToContent         = SizeToContent.Height;
        CanResize             = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Padding               = new Thickness(16, 14);
        this.Bind(BackgroundProperty, new DynamicResourceExtension("AppBarBg"));

        var shortcuts = new (string Key, string En, string Es)[]
        {
            ("Tab",          "Toggle channel panel",         "Panel de canales"),
            ("Esc",          "Close panel / exit fullscreen","Cerrar / salir pantalla"),
            ("F",            "Toggle fullscreen",            "Pantalla completa"),
            ("P",            "Play / Stop",                  "Play / Stop"),
            ("U",            "Load from URL",                "Cargar URL"),
            ("L",            "Load from file",               "Cargar archivo"),
            ("R",            "Record / Stop recording",      "Grabar / Stop"),
            ("M",            "Mute / Unmute",                "Silenciar / Activar"),
            ("H",            "Show this help",               "Mostrar ayuda"),
            ("A",            "Next aspect ratio",            "Siguiente relaci\u00f3n aspecto"),
            ("S",            "Next audio track",             "Siguiente pista de audio"),
            ("T",            "Next subtitle track",          "Siguiente subt\u00edtulo"),
            ("Num +",        "Volume +5%",                   "Volumen +5%"),
            ("Num \u2212",   "Volume \u22125%",              "Volumen \u22125%"),
            ("Scroll wheel", "Adjust volume",                "Ajustar volumen"),
            ("Middle click", "Toggle mute",                  "Silenciar"),
            ("Double click", "Toggle fullscreen",            "Pantalla completa"),
            ("Right click",  "Context menu",                 "Men\u00fa contextual"),
        };

        // ── Header row
        var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var hKey  = Cell(es ? "Tecla" : "Key",  130, bold: true);
        var hDesc = Cell(es ? "Descripci\u00f3n" : "Description", bold: true);
        DockPanel.SetDock(hKey, Dock.Left);
        headerRow.Children.Add(hKey);
        headerRow.Children.Add(hDesc);

        // ── Separator line
        var sep = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 6) };
        sep.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppButtonBorder"));

        // ── Shortcut rows
        var rowsPanel = new StackPanel { Spacing = 3 };
        foreach (var sc in shortcuts)
        {
            var row    = new DockPanel();
            var keyCell  = Cell(sc.Key, 130, mono: true);
            var descCell = Cell(es ? sc.Es : sc.En);
            DockPanel.SetDock(keyCell, Dock.Left);
            row.Children.Add(keyCell);
            row.Children.Add(descCell);
            rowsPanel.Children.Add(row);
        }

        // ── Close button
        var closeBtn = new Button
        {
            Content             = es ? "Cerrar" : "Close",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 14, 0, 0),
        };
        closeBtn.Click += (_, _) => Close();

        var root = new StackPanel();
        root.Children.Add(headerRow);
        root.Children.Add(sep);
        root.Children.Add(rowsPanel);
        root.Children.Add(closeBtn);
        Content = root;
    }

    private static TextBlock Cell(string text, double width = -1, bool bold = false, bool mono = false)
    {
        var tb = new TextBlock
        {
            Text       = text,
            Margin     = new Thickness(0, 2),
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        };
        if (width > 0)  tb.Width      = width;
        if (mono)       tb.FontFamily = new FontFamily("Consolas,Menlo,monospace");
        return tb;
    }
}
