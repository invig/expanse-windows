using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// The full-screen window the canvas is drawn in: topmost, out of the taskbar and Alt-Tab,
/// hidden at 100%. It paints the backdrop and labels (double-buffered GDI); the live window
/// pictures are DWM thumbnails composed on top of that. Input goes to `Handler`.
sealed class Overlay
{
    public const uint WM_COMMAND_REQUEST = WM_APP + 1;

    public readonly nint Hwnd;
    readonly WndProc proc; // kept alive: Windows holds a pointer to it
    public Func<uint, nint, nint, nint?>? Handler;
    public Action<nint, int, int>? Painter;

    public Overlay()
    {
        proc = WindowProc;
        var instance = RegisterWindowClass("ExpanseOverlay", proc);
        Hwnd = CreateWindowEx((uint)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW), "ExpanseOverlay", "Expanse",
            unchecked((uint)WS_POPUP), 0, 0, 100, 100, 0, 0, instance, 0);
        if (Hwnd == 0) throw new InvalidOperationException("couldn't create the overlay window");
        int round = 1; // DWMWCP_DONOTROUND: square corners for a full-screen surface
        Dwm.DwmSetWindowAttribute(Hwnd, Dwm.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    public bool IsVisible => IsWindowVisible(Hwnd);

    public void Show(RECT monitor, bool activate)
    {
        SetWindowPos(Hwnd, HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, monitor.Height,
            SWP_SHOWWINDOW | (activate ? 0 : SWP_NOACTIVATE));
        UpdateWindow(Hwnd);
    }

    public void Hide() => ShowWindow(Hwnd, SW_HIDE);

    public void Invalidate() => InvalidateRect(Hwnd, 0, false);

    public void Repaint() { InvalidateRect(Hwnd, 0, false); UpdateWindow(Hwnd); }

    nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_ERASEBKGND:
                    return 1;
                case WM_PAINT:
                    Paint(hwnd);
                    return 0;
            }
            var handled = Handler?.Invoke(msg, wParam, lParam);
            if (handled.HasValue) return handled.Value;
        }
        catch (Exception e)
        {
            Log.Info($"overlay: message 0x{msg:X} threw {e}");
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    void Paint(nint hwnd)
    {
        nint hdc = BeginPaint(hwnd, out var ps);
        GetClientRect(hwnd, out var rc);
        nint mem = Gdi32.CreateCompatibleDC(hdc);
        nint bmp = Gdi32.CreateCompatibleBitmap(hdc, rc.Width, rc.Height);
        nint old = Gdi32.SelectObject(mem, bmp);
        try
        {
            Painter?.Invoke(mem, rc.Width, rc.Height);
            Gdi32.BitBlt(hdc, 0, 0, rc.Width, rc.Height, mem, 0, 0, Gdi32.SRCCOPY);
        }
        finally
        {
            Gdi32.SelectObject(mem, old);
            Gdi32.DeleteObject(bmp);
            Gdi32.DeleteDC(mem);
            EndPaint(hwnd, ref ps);
        }
    }
}
