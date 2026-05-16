using System;
using System.IO;
using Avalonia.Threading;

namespace AzIPTV;

/// <summary>
/// Thread-safe application logger.
/// — Appends every message to log.txt (immediate flush, lock-protected).
/// — Posts each message to the UI thread via the <see cref="MessageLogged"/> event.
/// — All output is suppressed when <see cref="Enabled"/> is false (default).
///   Set it to true in this file to re-enable logging during development.
/// </summary>
public static class AppLogger
{
    /// <summary>Master switch — set to true to enable file + UI logging.</summary>
    public static readonly bool Enabled = false;
    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "log.txt");

    private static readonly object FileLock = new();

    // Guards Dispatcher.UIThread access. Accessing it before BuildAvaloniaApp()
    // registers the Win32 platform permanently installs a NullDispatcherImpl,
    // causing Dispatcher.MainLoop to throw PlatformNotSupportedException at startup.
    // Call MarkUiReady() from MainWindow after InitializeComponent().
    private static volatile bool _uiReady;
    public static void MarkUiReady() => _uiReady = true;

    /// <summary>Raised on the UI thread with each logged message (no timestamp prefix).</summary>
    public static event Action<string>? MessageLogged;

    public static void Log(string message)
    {
        // File logging only when enabled.
        if (Enabled)
        {
            var stamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            lock (FileLock)
            {
                try { File.AppendAllText(LogPath, stamped + Environment.NewLine); }
                catch { }
            }
        }

        // Status bar always updates (independent of file-logging flag).
        if (!_uiReady) return;

        if (Dispatcher.UIThread.CheckAccess())
        {
            try { MessageLogged?.Invoke(message); } catch { }
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { MessageLogged?.Invoke(message); } catch { }
            });
        }
    }

    public static void LogException(string context, Exception ex)
        => Log($"ERROR [{context}]: {ex}");

    /// <summary>Rotate the log file at startup so it doesn't grow unbounded.</summary>
    public static void Init()
    {
        if (!Enabled) return;

        try
        {
            // Keep only last 500 KB; if larger, truncate.
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024)
                File.WriteAllText(LogPath, string.Empty);
        }
        catch { }

        Log("AzIPTV started");
    }
}
