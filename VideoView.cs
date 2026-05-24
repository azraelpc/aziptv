using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AzIPTV;

/// <summary>
/// NativeControlHost that renders a Win32 HWND for LibVLC.
/// Mouse double-click and right-click are captured via a WH_MOUSE_LL low-level
/// global hook, which fires before VLC's own child windows can swallow events.
/// </summary>
public class VideoView : NativeControlHost
{
    private IntPtr _hwnd       = IntPtr.Zero;
    private IntPtr _hookHandle = IntPtr.Zero;
    private IntPtr _parentHwnd = IntPtr.Zero;
    private IntPtr _rootHwnd   = IntPtr.Zero;  // top-level window HWND for foreground check

    // Keep delegate alive — GC must not collect it while the hook is active.
    private LowLevelMouseProcDelegate?    _hookDelegate;
    private LowLevelKeyboardProcDelegate? _keyboardHookDelegate;
    private IntPtr _keyboardHookHandle = IntPtr.Zero;

    // Set to true while TrackPopupMenu is running so the mouse hook ignores
    // the left-click that dismisses the popup (otherwise it would arm the
    // single-click timer and re-toggle the side panel immediately after).
    public volatile bool SuppressMouseHook;

    // WndProc subclass — used only to paint the dark background before VLC renders.
    private WndProcDelegate? _subclassDelegate;
    private IntPtr _oldWndProc = IntPtr.Zero;
    private IntPtr _darkBrush  = IntPtr.Zero;

    // DispatcherTimer used to distinguish a single click from the first half of a double-click.
    // Runs on the UI thread — access is always safe there.
    private DispatcherTimer? _clickTimer;

    public IntPtr Hwnd => _hwnd;

    /// <summary>Fired on the UI thread immediately after the native HWND is created.</summary>
    public event Action<IntPtr>? HwndCreated;

    /// <summary>Fired on the UI thread after a confirmed single click (no second click within GetDoubleClickTime ms).</summary>
    public event Action? VideoSingleTapped;

    /// <summary>Fired on the UI thread when the user double-clicks over the video surface.</summary>
    public event Action? VideoDoubleTapped;

    /// <summary>Fired on the UI thread on right mouse button release; args are screen X/Y.</summary>
    public event Action<int, int>? VideoContextMenu;

    /// <summary>Fired on the UI thread when a key is pressed while our app is the foreground window.
    /// The argument is the Win32 virtual-key code.</summary>
    public event Action<uint>? VideoKeyPressed;

    /// <summary>Fired on the UI thread when the mouse wheel is scrolled over the video.
    /// Positive delta = scroll up; negative = scroll down.</summary>
    public event Action<int>? VideoMouseWheel;

    /// <summary>Fired on the UI thread when the middle mouse button is released over the video.</summary>
    public event Action? VideoMiddleClicked;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _hwnd = NativeMethods.CreateWindowEx(
            0, "STATIC", string.Empty,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE |
            NativeMethods.WS_CLIPSIBLINGS | NativeMethods.WS_CLIPCHILDREN,
            0, 0, 1, 1,
            parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        _parentHwnd = parent.Handle;

        // Walk up to the root (top-level) HWND. GetForegroundWindow() returns
        // the top-level window, so we need the same handle for comparison.
        IntPtr h = parent.Handle, p;
        while ((p = NativeMethods.GetParent(h)) != IntPtr.Zero) h = p;
        _rootHwnd = h;

        // Subclass to paint dark grey before VLC renders its first frame.
        _darkBrush = NativeMethods.CreateSolidBrush(0x00505050); // BGR dark grey
        _subclassDelegate = VideoWndProc;
        _oldWndProc = NativeMethods.SetWindowLongPtr(
            _hwnd, NativeMethods.GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_subclassDelegate));

        // Install a low-level mouse hook so we receive mouse events even when
        // VLC's DirectX child windows are on top and swallowing all Win32 messages.
        _hookDelegate = MouseHookProc;
        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            Marshal.GetFunctionPointerForDelegate(_hookDelegate),
            IntPtr.Zero, 0);

        // Install a low-level keyboard hook so we receive key presses even when
        // VLC's child HWNDs have focus (they intercept WM_KEYDOWN before Avalonia).
        _keyboardHookDelegate = KeyboardHookProc;
        _keyboardHookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            Marshal.GetFunctionPointerForDelegate(_keyboardHookDelegate),
            IntPtr.Zero, 0);

        HwndCreated?.Invoke(_hwnd);
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_oldWndProc != IntPtr.Zero && control.Handle != IntPtr.Zero)
            NativeMethods.SetWindowLongPtr(control.Handle, NativeMethods.GWLP_WNDPROC, _oldWndProc);
        if (_darkBrush != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_darkBrush);
            _darkBrush = IntPtr.Zero;
        }
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
        }
        if (control.Handle != IntPtr.Zero)
            NativeMethods.DestroyWindow(control.Handle);
        _hwnd = IntPtr.Zero;
    }

    private IntPtr VideoWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_ERASEBKGND && _darkBrush != IntPtr.Zero)
        {
            NativeMethods.GetClientRect(hWnd, out var rect);
            NativeMethods.FillRect(wParam, ref rect, _darkBrush);
            return new IntPtr(1);
        }
        return NativeMethods.CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _hwnd != IntPtr.Zero && !SuppressMouseHook)
        {
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

            // Only act when the cursor is over our video surface or any child of it
            // (VLC creates child HWNDs inside ours for its rendering pipeline).
            if (IsDescendantOfVideoHwnd(NativeMethods.WindowFromPoint(data.pt)))
            {
                int msg = wParam.ToInt32();

                if (msg == NativeMethods.WM_LBUTTONDOWN)
                {
                    // All DispatcherTimer operations must run on the UI thread.
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (_clickTimer is not null)
                            {
                                // Second click arrived before the timer fired → double-click.
                                _clickTimer.Stop();
                                _clickTimer = null;
                                VideoDoubleTapped?.Invoke();
                            }
                            else
                            {
                                // First click → arm the timer.
                                uint dblMs = NativeMethods.GetDoubleClickTime();
                                _clickTimer = new DispatcherTimer
                                    { Interval = TimeSpan.FromMilliseconds(dblMs) };
                                _clickTimer.Tick += (_, _) =>
                                {
                                    _clickTimer!.Stop();
                                    _clickTimer = null;
                                    VideoSingleTapped?.Invoke();
                                };
                                _clickTimer.Start();
                            }
                        }
                        catch (Exception ex) { AppLogger.LogException("VideoClick", ex); }
                    });
                }
                else if (msg == NativeMethods.WM_MOUSEMOVE)
                {
                    // VLC's child HWNDs hide the system cursor — restore the arrow on every move.
                    NativeMethods.SetCursor(NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_ARROW));
                }
                else if (msg == NativeMethods.WM_RBUTTONUP)
                {
                    int sx = data.pt.x, sy = data.pt.y;
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { VideoContextMenu?.Invoke(sx, sy); }
                        catch (Exception ex) { AppLogger.LogException("VideoContextMenu", ex); }
                    });
                }
                else if (msg == NativeMethods.WM_MOUSEWHEEL)
                {
                    // High word of mouseData is the signed wheel delta.
                    int delta = (int)(short)((data.mouseData >> 16) & 0xFFFF);
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { VideoMouseWheel?.Invoke(delta); }
                        catch (Exception ex) { AppLogger.LogException("VideoMouseWheel", ex); }
                    });
                }
                else if (msg == NativeMethods.WM_MBUTTONUP)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { VideoMiddleClicked?.Invoke(); }
                        catch (Exception ex) { AppLogger.LogException("VideoMiddleClicked", ex); }
                    });
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>Returns true if <paramref name="hwnd"/> is our video HWND or any of its descendants.</summary>
    private bool IsDescendantOfVideoHwnd(IntPtr hwnd)
    {
        IntPtr cur = hwnd;
        while (cur != IntPtr.Zero)
        {
            if (cur == _hwnd) return true;
            cur = NativeMethods.GetParent(cur);
        }
        return false;
    }

    private delegate IntPtr LowLevelMouseProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _hwnd != IntPtr.Zero)
        {
            int msg = wParam.ToInt32();
            // Only WM_KEYDOWN / WM_SYSKEYDOWN; ignore key-up events.
            if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
            {
                // Only fire when a VLC child HWND has focus, NOT when Avalonia's
                // own window has focus (which would double-fire alongside Avalonia's
                // own KeyDown event).
                IntPtr focused = NativeMethods.GetFocus();
                if (IsDescendantOfVideoHwnd(focused))
                {
                    var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    uint vk = data.vkCode;
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { VideoKeyPressed?.Invoke(vk); }
                        catch (Exception ex) { AppLogger.LogException("VideoKeyPressed", ex); }
                    });
                }
            }
        }
        return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }
}

internal static class NativeMethods
{
    public const uint WS_CHILD        = 0x40000000;
    public const uint WS_VISIBLE      = 0x10000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_CLIPCHILDREN = 0x02000000;
    public const int  GWLP_WNDPROC    = -4;

    public const int  WH_MOUSE_LL    = 14;
    public const int  WH_KEYBOARD_LL = 13;
    public const int  WM_KEYDOWN     = 0x0100;
    public const int  WM_SYSKEYDOWN  = 0x0104;
    public const int  WM_MOUSEMOVE   = 0x0200;
    public const int  WM_LBUTTONDOWN = 0x0201;
    public const int  WM_RBUTTONUP   = 0x0205;
    public const int  WM_MBUTTONUP   = 0x0208;
    public const int  WM_MOUSEWHEEL  = 0x020A;
    public const uint WM_ERASEBKGND  = 0x0014;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT   pt;
        public uint    mouseData;
        public uint    flags;
        public uint    time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint    vkCode;
        public uint    scanCode;
        public uint    flags;
        public uint    time;
        public UIntPtr dwExtraInfo;
    }

    // ── Window creation/destruction ──────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    // ── WndProc subclassing ──────────────────────────────────────────────────

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    public static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    // ── Low-level mouse hook ─────────────────────────────────────────────────

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowsHookEx(int idHook, IntPtr lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();

    // ── Native context menu ──────────────────────────────────────────────────

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    public static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);

    // ── Window Z-order ───────────────────────────────────────────────────────

    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE     = 0x0001;
    public const uint SWP_NOMOVE     = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    // ── Cursor ────────────────────────────────────────────────────────────────

    public static readonly IntPtr IDC_ARROW = new(32512);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    // ── DWM caption button theming ────────────────────────────────────────
    // DWMWA_USE_IMMERSIVE_DARK_MODE (20) makes DWM draw white min/max/close
    // glyphs, which is needed when our title bar has a dark background.
    // Must be called after the HWND is created (i.e. in or after Opened).
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
