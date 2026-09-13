using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;

namespace AzIPTV;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            EventHandler? closeSplash = null;
            closeSplash = (_, _) =>
            {
                mainWindow.Activated -= closeSplash;
                Dispatcher.UIThread.Post(NativeSplash.Close, DispatcherPriority.Background);
            };
            mainWindow.Activated += closeSplash;
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}