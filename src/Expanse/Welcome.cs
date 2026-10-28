using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// Welcome & Shortcuts: shown on first launch and from the tray, as on the Mac. The Mac's
/// walkthrough is mostly macOS permissions, which Windows doesn't ask for, so this is its first
/// and last pages: what Expanse is, the shortcuts, where the hot corner is, and anything in the
/// way (a shortcut another app holds).
static class Welcome
{
    static nint hwnd;
    static WndProc? proc; // kept alive: Windows holds a pointer to it
    static RECT button;
    static string corner = HotCorner.Default;
    static IReadOnlyList<string> problems = [];

    static readonly (string keys, string what)[] Shortcuts =
    {
        ("Ctrl + Alt + `", "Zoom out to every window, and back"),
        ("Click", "Dive into a window"),
        ("Ctrl + Alt + ← → ↑ ↓", "Hop to the nearest window that way"),
        ("Ctrl + Alt + C", "Centre the window you're in"),
        ("Wheel  ·  Ctrl + wheel", "Move around the canvas  ·  zoom"),
        ("Space + drag", "Hold Space and drag to move around"),
        ("Drag", "Move windows; they snap into place"),
        ("Shift + click", "Select several, then drag or close them together"),
        ("T", "Tidy up: line up each group, where it is"),
    };

    /// `hotCorner`: the corner in use (a HotCorner key); `trouble`: shortcuts another app holds.
    /// Returns the window, for the caller to bring to the front.
    public static nint Show(string hotCorner, IReadOnlyList<string> trouble)
    {
        corner = hotCorner;
        problems = trouble;
        if (hwnd != 0 && IsWindow(hwnd))
        {
            InvalidateRect(hwnd, 0, true);
            return hwnd;
        }
        proc ??= WindowProc;
        var instance = RegisterWindowClass("ExpanseWelcome", proc);
        var (monitor, work) = PrimaryMonitor();
        double k = PrimaryDpi() / 96.0;
        int w = (int)(600 * k), h = (int)(660 * k);
        hwnd = CreateWindowEx(0, "ExpanseWelcome", "Welcome to Expanse", 0x00C00000 | 0x00080000 | 0x00020000, // caption, system menu, minimise
            work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 2, w, h, 0, 0, instance, 0);
        if (hwnd == 0) { Log.Info("welcome: couldn't open"); return 0; }
        ShowWindow(hwnd, SW_SHOWNORMAL);
        Log.Info("welcome & shortcuts shown");
        return hwnd;
    }

    static nint WindowProc(nint h, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                Paint(h);
                return 0;
            case WM_ERASEBKGND:
                return 1;
            case WM_LBUTTONUP:
            {
                var p = new POINT((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                if (p.X >= button.Left && p.X < button.Right && p.Y >= button.Top && p.Y < button.Bottom) DestroyWindow(h);
                return 0;
            }
            case WM_KEYDOWN when (int)wParam is VK_RETURN or VK_ESCAPE:
                DestroyWindow(h);
                return 0;
            case WM_DPICHANGED:
            {
                var r = System.Runtime.InteropServices.Marshal.PtrToStructure<RECT>(lParam);
                SetWindowPos(h, 0, r.Left, r.Top, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);
                return 0;
            }
            case WM_DESTROY:
                hwnd = 0;
                return 0;
        }
        return DefWindowProc(h, msg, wParam, lParam);
    }

    static void Paint(nint h)
    {
        nint hdc = BeginPaint(h, out var ps);
        GetClientRect(h, out var rc);
        double k = GetDpiForWindow(h) / 96.0;
        int S(double v) => (int)Math.Round(v * k);
        nint mem = Gdi32.CreateCompatibleDC(hdc);
        nint bmp = Gdi32.CreateCompatibleBitmap(hdc, rc.Width, rc.Height);
        nint oldBmp = Gdi32.SelectObject(mem, bmp);
        var fonts = new List<nint>();
        nint Font(double size, int weight)
        {
            nint f = Gdi32.CreateFont(-S(size), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            fonts.Add(f);
            return f;
        }
        nint white = Gdi32.CreateSolidBrush(Gdi32.RGB(255, 255, 255));
        nint keycap = Gdi32.CreateSolidBrush(Gdi32.RGB(240, 241, 244));
        nint blue = Gdi32.CreateSolidBrush(Gdi32.RGB(44, 120, 220));
        var bg = new RECT(0, 0, rc.Width, rc.Height);
        FillRect(mem, ref bg, white);
        Gdi32.SetBkMode(mem, Gdi32.TRANSPARENT);
        int x = S(36), right = rc.Width - S(36), y = S(30);

        int Text(string text, nint font, uint colour, int top, int left, int width, uint flags = 0)
        {
            Gdi32.SelectObject(mem, font);
            Gdi32.SetTextColor(mem, colour);
            var r = new RECT(left, top, left + width, top + S(400));
            DrawText(mem, text, -1, ref r, DT_WORDBREAK | DT_NOPREFIX | DT_CALCRECT | flags);
            int height = r.Height;
            r = new RECT(left, top, left + width, top + height);
            DrawText(mem, text, -1, ref r, DT_WORDBREAK | DT_NOPREFIX | flags);
            return height;
        }

        uint ink = Gdi32.RGB(28, 28, 30), quiet = Gdi32.RGB(100, 100, 108), accent = Gdi32.RGB(44, 120, 220);
        y += Text("Welcome to Expanse", Font(12, 600), accent, y, x, right - x) + S(6);
        y += Text("Your PC, on an infinite canvas.", Font(24, 600), ink, y, x, right - x) + S(8);
        y += Text("Every window gets a spot on one endless desktop. Zoom out to see them all, then click one to dive in.",
            Font(13.5, 400), quiet, y, x, right - x) + S(22);

        y += Text("SHORTCUTS", Font(10.5, 600), quiet, y, x, right - x) + S(8);
        nint keyFont = Font(12, 600), whatFont = Font(13, 400);
        int keyWidth = S(190);
        Gdi32.SelectObject(mem, Gdi32.GetStockObject(Gdi32.NULL_PEN));
        foreach (var (keys, what) in Shortcuts)
        {
            Gdi32.SelectObject(mem, keyFont);
            var measure = new RECT(0, 0, S(400), S(40));
            DrawText(mem, keys, -1, ref measure, DT_SINGLELINE | DT_NOPREFIX | DT_CALCRECT);
            int pillW = measure.Width + S(16), pillH = S(24);
            Gdi32.SelectObject(mem, keycap);
            Gdi32.RoundRect(mem, x, y, x + pillW, y + pillH, S(8), S(8));
            var keyRect = new RECT(x, y, x + pillW, y + pillH);
            Gdi32.SetTextColor(mem, ink);
            DrawText(mem, keys, -1, ref keyRect, DT_SINGLELINE | DT_VCENTER | DT_CENTER | DT_NOPREFIX);
            Gdi32.SelectObject(mem, whatFont);
            var whatRect = new RECT(x + keyWidth, y, right, y + pillH);
            DrawText(mem, what, -1, ref whatRect, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
            y += pillH + S(8);
        }
        y += S(12);

        string cornerName = HotCorner.Choices.FirstOrDefault(c => c.key == corner).title ?? "";
        string tryIt = corner == "off"
            ? "Press Ctrl + Alt + ` to zoom out, then click any window to dive back in."
            : $"Rest the pointer in {(corner == "all" ? "any" : "the " + cornerName.ToLowerInvariant())} corner of the screen (or press Ctrl + Alt + `), then click any window to dive back in.";
        y += Text(tryIt, Font(13.5, 600), ink, y, x, right - x) + S(10);
        foreach (var p in problems)
            y += Text(p, Font(12.5, 400), Gdi32.RGB(176, 72, 0), y, x, right - x) + S(6);
        y += Text("Windows puts new tray icons under ^ in the taskbar: drag Expanse's onto the taskbar to keep it in reach. " +
                  "Find this again under the tray icon › Welcome & Shortcuts.", Font(12, 400), quiet, y, x, right - x);

        // The one thing to do next.
        Gdi32.SelectObject(mem, Font(13.5, 600));
        var label = new RECT(0, 0, S(400), S(40));
        DrawText(mem, "Start using Expanse", -1, ref label, DT_SINGLELINE | DT_NOPREFIX | DT_CALCRECT);
        int bw = label.Width + S(36), bh = S(38);
        button = new RECT(right - bw, rc.Height - S(26) - bh, right, rc.Height - S(26));
        Gdi32.SelectObject(mem, blue);
        Gdi32.RoundRect(mem, button.Left, button.Top, button.Right, button.Bottom, bh, bh);
        Gdi32.SetTextColor(mem, Gdi32.RGB(255, 255, 255));
        var b = button;
        DrawText(mem, "Start using Expanse", -1, ref b, DT_SINGLELINE | DT_VCENTER | DT_CENTER | DT_NOPREFIX);

        Gdi32.BitBlt(hdc, 0, 0, rc.Width, rc.Height, mem, 0, 0, Gdi32.SRCCOPY);
        Gdi32.SelectObject(mem, oldBmp);
        Gdi32.DeleteObject(bmp);
        Gdi32.DeleteDC(mem);
        foreach (var f in fonts) Gdi32.DeleteObject(f);
        Gdi32.DeleteObject(white);
        Gdi32.DeleteObject(keycap);
        Gdi32.DeleteObject(blue);
        EndPaint(h, ref ps);
    }
}
