using Expanse.Native;
using static Expanse.Native.User32;

namespace Spikes;

/// Plain Win32 windows that repaint a frame counter and a moving bar ~30 times a second: a
/// known-live source for picture benches and the parking test, and a classic GDI app.
static class Sources
{
    static readonly WndProc proc = WindowProc;
    static readonly Dictionary<nint, int> frames = new();

    public static int Run(int count)
    {
        var instance = RegisterWindowClass("ExpanseSpikeSource", proc);
        var (monitor, _) = PrimaryMonitor();
        var rng = new Random(7);
        for (int i = 0; i < count; i++)
        {
            int w = 480 + rng.Next(5) * 80, h = 320 + rng.Next(4) * 60;
            int x = monitor.Left + 40 + (i % 6) * 760, y = monitor.Top + 40 + (i / 6) * 560;
            nint hwnd = CreateWindowEx(0, "ExpanseSpikeSource", $"Source {i + 1}",
                (uint)(WS_OVERLAPPEDWINDOW | WS_VISIBLE), x, y, w, h, 0, 0, instance, 0);
            frames[hwnd] = 0;
            SetTimer(hwnd, 1, 33, 0);
        }
        Out.Log($"sources: {count} windows up");
        while (GetMessage(out var m, 0, 0, 0) > 0)
        {
            TranslateMessage(ref m);
            DispatchMessage(ref m);
        }
        return 0;
    }

    static nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_TIMER:
                InvalidateRect(hwnd, 0, false);
                return 0;
            case WM_ERASEBKGND:
                return 1;
            case WM_PAINT:
            {
                int n = frames[hwnd] = frames.GetValueOrDefault(hwnd) + 1;
                nint hdc = BeginPaint(hwnd, out var ps);
                GetClientRect(hwnd, out var rc);
                var hue = (int)(hwnd & 0xFF);
                nint bg = Gdi32.CreateSolidBrush(Gdi32.RGB((byte)(40 + hue % 60), 44, (byte)(70 + hue % 90)));
                FillRect(hdc, ref rc, bg);
                Gdi32.DeleteObject(bg);
                var bar = new RECT((n * 6) % Math.Max(1, rc.Width - 40), rc.Height - 30, 0, rc.Height - 10);
                bar.Right = bar.Left + 40;
                nint fg = Gdi32.CreateSolidBrush(Gdi32.RGB(255, 200, 60));
                FillRect(hdc, ref bar, fg);
                Gdi32.DeleteObject(fg);
                nint font = Gdi32.CreateFont(-rc.Height / 3, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
                nint old = Gdi32.SelectObject(hdc, font);
                Gdi32.SetBkMode(hdc, Gdi32.TRANSPARENT);
                Gdi32.SetTextColor(hdc, Gdi32.RGB(255, 255, 255));
                DrawText(hdc, n.ToString(), -1, ref rc, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
                Gdi32.SelectObject(hdc, old);
                Gdi32.DeleteObject(font);
                EndPaint(hwnd, ref ps);
                return 0;
            }
            case WM_DESTROY:
                frames.Remove(hwnd);
                if (frames.Count == 0) PostQuitMessage(0);
                return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
