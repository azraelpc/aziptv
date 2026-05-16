using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AzIPTV;

/// <summary>
/// Shows a lightweight native Win32 splash window before Avalonia starts,
/// giving immediate visual feedback during the slow first-run JIT / LibVLC load.
/// The [ModuleInitializer] fires before Main(), the earliest point in managed code.
/// </summary>
internal static class NativeSplash
{
    private static IntPtr           _hwnd;
    private static WndProcDelegate? _delegate; // keep alive so GC doesn't collect it
    private static float            _scale = 1f;

    private const string ClassName = "AzIPTVSplash";
    // Base dimensions at 96 DPI; scaled at runtime.
    private const int BaseW = 300, BaseH = 100;

    // Win32 colors are 0x00BBGGRR.  These are all greys so BGR == RGB.
    private const uint BG       = 0x00_22_22_22;
    private const uint FG       = 0x00_EE_EE_EE;
    private const uint BORDER   = 0x00_44_44_44;
    private const uint SUBTITLE = 0x00_99_99_99;

    // ── Module initializer ────────────────────────────────────────────────────
    // Runs before Main() is entered — the earliest possible moment.

    [ModuleInitializer]
    internal static void EarlyInit()
    {
        try { Show(); }
        catch { /* Splash is best-effort; never block startup. */ }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public static void Show()
    {
        // Declare per-monitor DPI awareness before creating any window so that
        // all coordinates and sizes are in physical pixels.  Avalonia calls the
        // same API slightly later; a second call is a no-op on Windows 10+.
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2

        var hInst = GetModuleHandle(null);
        _delegate = WndProc;

        // ── Find the right monitor ──────────────────────────────────────────
        // Use the monitor that contains the cursor — that is where the user
        // double-clicked the exe, so the splash appears in front of them.
        GetCursorPos(out var cursorPt);
        var hMonitor = MonitorFromPoint(cursorPt, 2); // MONITOR_DEFAULTTONEAREST

        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMonitor, ref mi);

        // ── DPI scaling ─────────────────────────────────────────────────────
        // GetDpiForMonitor returns the effective DPI for that specific monitor.
        int hr = GetDpiForMonitor(hMonitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _);
        if (hr != 0 || dpiX == 0) dpiX = 96; // fallback for unexpected failures
        _scale = dpiX / 96f;

        int w = (int)(BaseW * _scale);
        int h = (int)(BaseH * _scale);

        // ── Center on the monitor's work area (respects taskbar) ────────────
        int x = (mi.rcWork.Right  + mi.rcWork.Left - w) / 2;
        int y = (mi.rcWork.Bottom + mi.rcWork.Top  - h) / 2;

        var wc = new WNDCLASSEX
        {
            cbSize        = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_delegate),
            hInstance     = hInst,
            hCursor       = LoadCursor(IntPtr.Zero, 32512), // IDC_ARROW
            hbrBackground = CreateSolidBrush(BG),
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(
            0x00000088,                   // WS_EX_TOPMOST | WS_EX_TOOLWINDOW
            ClassName, null,
            unchecked((int)0x80000000),   // WS_POPUP
            x, y, w, h,
            IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, 5);  // SW_SHOW
            UpdateWindow(_hwnd);
        }
    }

    public static void Close()
    {
        if (_hwnd == IntPtr.Zero) return;
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    // ── Window procedure ──────────────────────────────────────────────────────

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0014: // WM_ERASEBKGND
                return new IntPtr(1); // handled — prevents flicker

            case 0x000F: // WM_PAINT
            {
                var ps  = new PAINTSTRUCT();
                var hdc = BeginPaint(hwnd, ref ps);
                GetClientRect(hwnd, out var rc);

                // Background
                var bgBrush = CreateSolidBrush(BG);
                FillRect(hdc, ref rc, bgBrush);
                DeleteObject(bgBrush);

                // 1-pixel border
                var borderBrush = CreateSolidBrush(BORDER);
                FrameRect(hdc, ref rc, borderBrush);
                DeleteObject(borderBrush);

                SetBkMode(hdc, 1); // TRANSPARENT

                // DPI-scaled fonts: title bold 22pt, subtitle regular 11pt.
                // Negative height = size in pixels; CLEARTYPE_QUALITY (5) for crisp text.
                var titleFont = CreateFont(-(int)(22 * _scale), 0, 0, 0, 700,
                    0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
                var subFont   = CreateFont(-(int)(11 * _scale), 0, 0, 0, 400,
                    0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

                // Title "AzIPTV" — upper 60 % of the window
                var oldFont = SelectObject(hdc, titleFont);
                SetTextColor(hdc, FG);
                var titleRc = new RECT { Left = 0, Top = (int)(10 * _scale), Right = rc.Right, Bottom = rc.Bottom * 6 / 10 };
                DrawText(hdc, "AzIPTV", -1, ref titleRc, 0x0025); // DT_CENTER | DT_VCENTER | DT_SINGLELINE

                // Subtitle "Loading…" — lower 40 %
                SelectObject(hdc, subFont);
                SetTextColor(hdc, SUBTITLE);
                var subRc = new RECT { Left = 0, Top = rc.Bottom * 6 / 10, Right = rc.Right, Bottom = rc.Bottom - (int)(8 * _scale) };
                DrawText(hdc, "Loading\u2026", -1, ref subRc, 0x0025);

                SelectObject(hdc, oldFont);
                DeleteObject(titleFont);
                DeleteObject(subFont);

                EndPaint(hwnd, ref ps);
                return IntPtr.Zero;
            }
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint   cbSize, style;
        public IntPtr lpfnWndProc;
        public int    cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string  lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int    fErase;
        public RECT   rcPaint;
        public int    fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor; // full monitor bounds
        public RECT rcWork;    // work area (excludes taskbar)
        public uint dwFlags;
    }

    [DllImport("user32.dll")]  static extern bool   SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")]  static extern bool   GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]  static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO lpmi);
    [DllImport("shcore.dll")] static extern int    GetDpiForMonitor(IntPtr hMon, uint dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(int dwExStyle, string lpClass, string? lpName, int dwStyle,
        int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lp);

    [DllImport("user32.dll")] static extern bool   ShowWindow(IntPtr hWnd, int nCmd);
    [DllImport("user32.dll")] static extern bool   UpdateWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool   DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool   EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool   GetClientRect(IntPtr hWnd, out RECT rc);
    [DllImport("user32.dll")] static extern bool   FillRect(IntPtr hDC, ref RECT rc, IntPtr hBr);
    [DllImport("user32.dll")] static extern bool   FrameRect(IntPtr hDC, ref RECT rc, IntPtr hBr);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int DrawText(IntPtr hdc, string text, int len, ref RECT rc, uint fmt);

    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr hInst, int lpName);
    [DllImport("gdi32.dll")]  static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")]  static extern bool   DeleteObject(IntPtr hObj);
    [DllImport("gdi32.dll")]  static extern uint   SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")]  static extern int    SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")]  static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFont(int cHeight, int cWidth, int cEscapement, int cOrientation,
        int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);
}
