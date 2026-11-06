using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// What the tray icon opens, in place of a menu, as on the Mac: a flyout with the canvas at a
/// glance (a live map, which opens it), the everyday commands and switches, the hot corner, earlier
/// layouts to go back to, and an update that's ready. The gear goes to a second page of the same
/// panel, Settings.
///
/// Drawn by hand with GDI+ into one bitmap, whole, each time something changes (a few milliseconds,
/// a few times a second at most): the regions you can click are worked out as it draws.
sealed class TrayPanel
{
    public sealed record State(bool Paused, bool Active, IReadOnlyList<RectD> Windows, RectD Viewport,
        bool AutoCentre, bool Wallpaper, bool Names, string Corner, string? Update, bool DetailedLogging, bool HasLogFile,
        bool OpenAtLogin, double Gap, string Motion, bool AutoUpdates, bool Admin);

    /// What it asks the canvas to do.
    public sealed record Actions(
        Action ToggleCanvas, Action<bool> SetPaused, Action CentreWindow, Action Tidy, Action RestoreAll,
        Action<LayoutStore.Snapshot> RestoreLayout, Action TakeSnapshot, Action AutoCentre, Action Wallpaper, Action Names,
        Action<string> Corner, Action InstallUpdate, Action DetailedLogging, Action CopyLogs, Action ShowLogFile, Action ReportIssue,
        Action<bool> OpenAtLogin, Action<double> Gap, Action<string> Motion, Action<bool> AutoUpdates, Action CheckForUpdates,
        Action AdminWindows, Action Welcome, Action Quit,
        /// Focus goes back to the window you were in (or the canvas): the panel is going away.
        Action RestoreFocus, Func<nint, string> Focus);

    /// How a click on something leaves the panel: switches and picks leave it open, to see them
    /// take; commands close it, as a menu would, and focus goes back unless what they do takes it.
    enum After { Stays, Closes, ClosesKeepingFocus }

    sealed record Hit(string Key, RectangleF Rect, Action Run, After After, string? Tip);

    sealed record Earlier(LayoutStore.Snapshot Snapshot, List<RectD> Windows);

    const float Width = 360, Margin = 10, Radius = 8;
    const nint RefreshTimer = 1;

    readonly Func<State> read;
    readonly Actions actions;
    readonly WndProc proc;
    nint hwnd;
    bool settingsPage;
    long closedAt = -10_000;
    /// The Troubleshooting menu is up: clicks in it aren't clicks outside the panel.
    bool menuOpen;
    string? hover, pressed;
    readonly List<Hit> hits = new();
    List<Earlier> earlier = new();
    float k = 1;
    int shownHeight;
    (int right, int bottom, int top, bool below) anchor;
    Color accent = Color.FromArgb(0, 120, 212);
    bool dark;
    Dictionary<string, Font> fonts = new();

    public bool IsShown => hwnd != 0 && IsWindowVisible(hwnd);

    /// A click elsewhere closes it: on the desktop, that click isn't also one that zooms out.
    public bool JustClosed => Environment.TickCount64 - closedAt < 1000;

    public TrayPanel(Func<State> read, Actions actions)
    {
        this.read = read;
        this.actions = actions;
        proc = WindowProc;
    }

    /// The click on the icon that took its focus, and so closed it, mustn't open it again.
    public void Toggle(RECT icon)
    {
        if (IsShown) Close(restoreFocus: true);
        else if (Environment.TickCount64 - closedAt > 500) Show(icon);
    }

    /// Above the tray icon `icon` (screen coordinates), or below it with the taskbar on top.
    public void Show(RECT icon)
    {
        if (hwnd == 0 && !Create()) return;
        settingsPage = false;
        hover = null;
        ReadTheme();
        LoadEarlier();
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        var at = new POINT((icon.Left + icon.Right) / 2, (icon.Top + icon.Bottom) / 2);
        nint monitor = MonitorFromPoint(at, MONITOR_DEFAULTTOPRIMARY);
        GetMonitorInfo(monitor, ref mi);
        k = (Shcore.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi : 96) / 96f;
        foreach (var f in fonts.Values) f.Dispose();
        fonts = new();
        var work = mi.rcWork;
        int gap = (int)(12 * k);
        bool below = work.Top > mi.rcMonitor.Top;
        int right = Math.Clamp(at.X + (int)(Width * k / 2), work.Left + (int)(Width * k) + gap, work.Right - gap);
        anchor = (right, work.Bottom - gap, work.Top + gap, below);
        shownHeight = 0;
        Render();
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        Log.Info($"panel: focus {actions.Focus(hwnd)}");
        SetTimer(hwnd, RefreshTimer, 1000, 0);
    }

    public void Close(bool restoreFocus)
    {
        if (!IsShown) return;
        closedAt = Environment.TickCount64;
        KillTimer(hwnd, RefreshTimer);
        // Before it goes: otherwise Windows gives focus to the next window in line, wherever that is.
        if (restoreFocus && GetForegroundWindow() == hwnd) actions.RestoreFocus();
        ShowWindow(hwnd, SW_HIDE);
    }

    /// Something changed from outside (an update ready, say).
    public void Refresh()
    {
        if (IsShown) Render();
    }

    bool Create()
    {
        var instance = RegisterWindowClass("ExpansePanel", proc);
        // A thick frame (with no non-client area, see WM_NCCALCSIZE) is what gets DWM's backdrop,
        // rounded corners and shadow.
        const uint WS_THICKFRAME = 0x00040000;
        hwnd = CreateWindowEx((uint)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW), "ExpansePanel", "Expanse",
            unchecked((uint)WS_POPUP) | WS_THICKFRAME, 0, 0, 100, 100, 0, 0, instance, 0);
        if (hwnd == 0)
        {
            Log.Info("panel: couldn't create its window");
            return false;
        }
        var margins = new Dwm.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Dwm.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        int round = 2; // DWMWCP_ROUND
        Dwm.DwmSetWindowAttribute(hwnd, Dwm.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int acrylic = 3; // DWMSBT_TRANSIENTWINDOW: the flyouts' own backdrop
        Dwm.DwmSetWindowAttribute(hwnd, Dwm.DWMWA_SYSTEMBACKDROP_TYPE, ref acrylic, sizeof(int));
        return true;
    }

    nint WindowProc(nint h, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_NCCALCSIZE when wParam != 0:
                    return 0;
                case WM_NCHITTEST:
                    return 1; // HTCLIENT
                case WM_ERASEBKGND:
                    return 1;
                case WM_PAINT:
                    Paint(h);
                    return 0;
                case WM_ACTIVATE when (wParam & 0xFFFF) == 0 && !menuOpen:
                    Close(restoreFocus: false);
                    return 0;
                case WM_TIMER when wParam == RefreshTimer:
                    Render();
                    return 0;
                case WM_MOUSEMOVE:
                {
                    var key = HitAt(lParam)?.Key;
                    if (key != hover)
                    {
                        hover = key;
                        Render();
                    }
                    var track = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 2 /* TME_LEAVE */, hwndTrack = h };
                    TrackMouseEvent(ref track);
                    return 0;
                }
                case WM_MOUSELEAVE:
                    if (hover != null)
                    {
                        hover = null;
                        Render();
                    }
                    return 0;
                case WM_LBUTTONDOWN:
                    pressed = HitAt(lParam)?.Key;
                    return 0;
                case WM_LBUTTONUP:
                {
                    var hit = HitAt(lParam);
                    bool same = hit != null && hit.Key == pressed;
                    pressed = null;
                    if (same) Click(hit!);
                    return 0;
                }
                case WM_KEYDOWN when (int)wParam == VK_ESCAPE:
                    // Back from Settings, then closed.
                    if (settingsPage)
                    {
                        settingsPage = false;
                        hover = null;
                        Render();
                    }
                    else Close(restoreFocus: true);
                    return 0;
                case WM_SETTINGCHANGE when lParam != 0 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet":
                    ReadTheme();
                    Refresh();
                    return 0;
            }
        }
        catch (Exception e) { Log.Info($"panel: {e}"); }
        return DefWindowProc(h, msg, wParam, lParam);
    }

    Hit? HitAt(nint lParam)
    {
        float x = (short)(lParam & 0xFFFF), y = (short)((lParam >> 16) & 0xFFFF);
        return hits.LastOrDefault(h => h.Rect.Contains(x, y));
    }

    void Click(Hit hit)
    {
        Log.Info($"panel: {hit.Key}");
        if (hit.After != After.Stays) Close(restoreFocus: hit.After == After.Closes);
        hit.Run();
        Refresh();
    }

    void ReadTheme()
    {
        using var theme = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        dark = theme?.GetValue("SystemUsesLightTheme") is int v && v == 0;
        int immersive = dark ? 1 : 0;
        if (hwnd != 0) Dwm.DwmSetWindowAttribute(hwnd, Dwm.DWMWA_USE_IMMERSIVE_DARK_MODE, ref immersive, sizeof(int));
        using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        // Stored as AABBGGRR.
        accent = dwm?.GetValue("AccentColor") is int c
            ? Color.FromArgb(255, c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF) : Color.FromArgb(0, 120, 212);
    }

    void LoadEarlier()
    {
        earlier = LayoutStore.Snapshots().Take(LayoutStore.SnapshotsKept)
            .Select(s => LayoutStore.Load(s) is SavedLayout l
                ? new Earlier(s, l.Windows.Where(w => !w.Away).Select(w => new RectD(w.X, w.Y, w.W, w.H)).ToList()) : null)
            .OfType<Earlier>().ToList();
    }

    // MARK: Drawing

    Bitmap? bitmap;

    /// The whole panel, into `bitmap`, and the window sized to fit it.
    void Render()
    {
        if (hwnd == 0) return;
        var state = read();
        int width = (int)Math.Ceiling(Width * k);
        // Measured first, then drawn: the same code, on a throwaway surface.
        using (var probe = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
        using (var g = Graphics.FromImage(probe))
        {
            hits.Clear();
            float needed = settingsPage ? DrawSettings(g, state, measuring: true, 0) : DrawMain(g, state, measuring: true, 0);
            int room = Math.Max(1, anchor.bottom - anchor.top);
            int height = (int)Math.Ceiling(Math.Min(needed, room));
            float squeeze = Math.Max(0, needed - room);
            hits.Clear();
            var fresh = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var g2 = Graphics.FromImage(fresh))
            {
                g2.SmoothingMode = SmoothingMode.AntiAlias;
                g2.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g2.Clear(Color.Transparent);
                using (var tone = new SolidBrush(dark ? Color.FromArgb(150, 32, 32, 32) : Color.FromArgb(170, 243, 243, 243)))
                    g2.FillRectangle(tone, 0, 0, width, height);
                if (settingsPage) DrawSettings(g2, state, measuring: false, squeeze); else DrawMain(g2, state, measuring: false, squeeze);
            }
            bitmap?.Dispose();
            bitmap = fresh;
            if (height != shownHeight)
            {
                shownHeight = height;
                int y = anchor.below ? anchor.top : anchor.bottom - height;
                SetWindowPos(hwnd, 0, anchor.right - width, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }
        InvalidateRect(hwnd, 0, false);
    }

    void Paint(nint h)
    {
        nint hdc = BeginPaint(h, out var ps);
        if (bitmap != null)
        {
            // With its alpha: DWM puts it over the backdrop.
            nint gdi = bitmap.GetHbitmap(Color.FromArgb(0));
            nint mem = Gdi32.CreateCompatibleDC(hdc);
            nint old = Gdi32.SelectObject(mem, gdi);
            Gdi32.BitBlt(hdc, 0, 0, bitmap.Width, bitmap.Height, mem, 0, 0, Gdi32.SRCCOPY);
            Gdi32.SelectObject(mem, old);
            Gdi32.DeleteDC(mem);
            Gdi32.DeleteObject(gdi);
        }
        EndPaint(h, ref ps);
    }

    Color Primary => dark ? Color.FromArgb(245, 255, 255, 255) : Color.FromArgb(235, 0, 0, 0);
    Color Secondary => dark ? Color.FromArgb(170, 255, 255, 255) : Color.FromArgb(160, 0, 0, 0);
    Color CardFill => dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255);
    Color CardHover => dark ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(215, 255, 255, 255);
    Color CardEdge => dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
    /// Links: the accent, lightened over a dark panel, where it's hard to read as it is.
    Color LinkInk => dark ? Color.FromArgb((accent.R + 255) / 2, (accent.G + 255) / 2, (accent.B + 255) / 2) : accent;

    /// A font at this size (at 100%), kept until the panel next opens.
    Font F(float size, bool semibold = false, string? family = null)
    {
        string name = family ?? (semibold ? "Segoe UI Semibold" : "Segoe UI");
        string key = $"{name}/{size}";
        if (!fonts.TryGetValue(key, out var f)) fonts[key] = f = new Font(name, size * k, FontStyle.Regular, GraphicsUnit.Pixel);
        return f;
    }

    /// Icons: Windows 11's font of them (Windows 10's, under another name, has the same ones).
    Font Icons(float size)
    {
        var f = F(size, family: "Segoe Fluent Icons");
        return f.Name == "Segoe Fluent Icons" ? f : F(size, family: "Segoe MDL2 Assets");
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    void Fill(Graphics g, RectangleF r, float radius, Color fill, Color? edge = null)
    {
        using var path = Rounded(r, radius * k);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        if (edge is not Color e) return;
        using var pen = new Pen(e, Math.Max(1, k));
        g.DrawPath(pen, path);
    }

    /// One line, cut off with an ellipsis at `width`.
    void Text(Graphics g, string text, Font font, Color color, float x, float y, float width, StringAlignment align = StringAlignment.Near)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Alignment = align, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(text, font, brush, new RectangleF(x, y, width, font.Size * 1.5f), format);
    }

    /// Wrapped text; returns its height.
    float Paragraph(Graphics g, string text, Font font, Color color, float x, float y, float width, bool measuring)
    {
        float h = g.MeasureString(text, font, (int)width).Height;
        if (!measuring)
        {
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, new RectangleF(x, y, width, h));
        }
        return h;
    }

    /// A card you can click: lit under the pointer.
    void Button(Graphics g, string key, RectangleF r, Action run, After after, string? tip = null, float radius = Radius, bool plain = false)
    {
        hits.Add(new Hit(key, r, run, after, tip));
        bool over = hover == key;
        if (plain && !over) return;
        Fill(g, r, radius, over ? CardHover : CardFill, plain ? null : CardEdge);
    }

    /// Windows 11's switch.
    void Switch(Graphics g, float right, float midY, bool on)
    {
        float w = 40 * k, h = 20 * k, x = right - w, y = midY - h / 2;
        var r = new RectangleF(x, y, w, h);
        if (on) Fill(g, r, 10, accent);
        else Fill(g, r, 10, Color.Transparent, Secondary);
        float d = (on ? 14 : 12) * k;
        var knob = new RectangleF(on ? x + w - d - 3 * k : x + 4 * k, midY - d / 2, d, d);
        using var brush = new SolidBrush(on ? Color.White : Secondary);
        g.FillEllipse(brush, knob);
    }

    void Pill(Graphics g, string key, RectangleF r, string title, bool selected, Action pick)
    {
        hits.Add(new Hit(key, r, pick, After.Stays, null));
        Fill(g, r, 6, selected ? accent : hover == key ? CardHover : CardFill, selected ? null : CardEdge);
        var format = StringAlignment.Center;
        Text(g, title, F(12), selected ? Color.White : Primary, r.X, r.Y + (r.Height - 18 * k) / 2, r.Width, format);
    }

    /// Key caps: "Ctrl", "Alt", "C".
    float Keys(Graphics g, string[] keys, float right, float midY)
    {
        float x = right;
        foreach (var key in keys.Reverse())
        {
            float w = Math.Max(g.MeasureString(key, F(10)).Width + 4 * k, 18 * k), h = 18 * k;
            x -= w;
            Fill(g, new RectangleF(x, midY - h / 2, w, h), 4, CardFill, CardEdge);
            Text(g, key, F(10), Secondary, x, midY - 7 * k, w, StringAlignment.Center);
            x -= 3 * k;
        }
        return x;
    }

    void SwitchRow(Graphics g, string key, ref float y, float inner, string title, bool on, Action toggle, string[]? keys = null, bool enabled = true)
    {
        float h = 34 * k;
        var r = new RectangleF(Margin * k, y, Width * k - 2 * Margin * k, h);
        if (enabled) Button(g, key, r, toggle, After.Stays, plain: true, radius: 6);
        Text(g, title, F(12), enabled ? Primary : Secondary, r.X + inner, y + (h - 18 * k) / 2, r.Width - 2 * inner - 110 * k);
        Switch(g, r.Right - inner, y + h / 2, on);
        if (keys != null) Keys(g, keys, r.Right - inner - 48 * k, y + h / 2);
        y += h;
    }

    /// The canvas at a glance: every window, and the part the screen shows, fitted to `box`.
    void Map(Graphics g, RectangleF box, IReadOnlyList<RectD> windows, RectD? viewport, float alpha = 1)
    {
        var all = windows.ToList();
        if (viewport is RectD v) all.Add(v);
        if (all.Count == 0) return;
        var bounds = all.Skip(1).Aggregate(all[0], (b, r) => b.Union(r));
        if (bounds.W <= 0 || bounds.H <= 0) return;
        float s = (float)Math.Min(box.Width / bounds.W, box.Height / bounds.H);
        float ox = box.X + (box.Width - (float)bounds.W * s) / 2, oy = box.Y + (box.Height - (float)bounds.H * s) / 2;
        RectangleF R(RectD r) => new(ox + (float)(r.X - bounds.X) * s, oy + (float)(r.Y - bounds.Y) * s, (float)r.W * s, (float)r.H * s);
        Color tone = dark ? Color.FromArgb((int)(90 * alpha), 255, 255, 255) : Color.FromArgb((int)(70 * alpha), 0, 0, 0);
        foreach (var w in windows) Fill(g, R(w), 2, tone);
        if (viewport is RectD view)
        {
            var r = R(view);
            Fill(g, r, 3, Color.FromArgb(40, accent), accent);
        }
    }

    // MARK: The main page

    float DrawMain(Graphics g, State s, bool measuring, float squeeze)
    {
        float m = Margin * k, w = Width * k - 2 * m, y = m, inner = 12 * k;

        // Expanse, what it's doing, and the switch that pauses it.
        Text(g, "Expanse", F(13, semibold: true), Primary, m + 4 * k, y, w - 60 * k);
        string status = s.Paused ? "Paused · windows back where they were"
            : $"On · {s.Windows.Count} window{(s.Windows.Count == 1 ? "" : "s")} on the canvas";
        Text(g, status, F(11), Secondary, m + 4 * k, y + 19 * k, w - 60 * k);
        Button(g, "pause", new RectangleF(m + w - 48 * k, y + 2 * k, 48 * k, 32 * k), () => actions.SetPaused(!s.Paused), After.Stays,
            s.Paused ? "Resume Expanse" : "Pause Expanse: windows go back where they were", plain: true, radius: 6);
        Switch(g, m + w - 4 * k, y + 18 * k, !s.Paused);
        y += 40 * k;

        // An update that's ready.
        if (s.Update is string update)
        {
            var banner = new RectangleF(m, y, w, 76 * k);
            Fill(g, banner, Radius, Color.FromArgb(46, accent), CardEdge);
            Text(g, "", Icons(18), accent, banner.X + inner, banner.Y + 11 * k, 24 * k);
            Text(g, $"Expanse {update} is ready", F(12, semibold: true), Primary, banner.X + 40 * k, banner.Y + 8 * k, w - 50 * k);
            Text(g, "It restarts, and your windows stay where they are.", F(11), Secondary, banner.X + 40 * k, banner.Y + 26 * k, w - 50 * k);
            var install = new RectangleF(banner.X + 40 * k, banner.Y + 46 * k, 130 * k, 24 * k);
            hits.Add(new Hit("install", install, actions.InstallUpdate, After.ClosesKeepingFocus, null));
            Fill(g, install, 6, hover == "install" ? Color.FromArgb(220, accent) : accent);
            Text(g, "Install and restart", F(12), Color.White, install.X, install.Y + 4 * k, install.Width, StringAlignment.Center);
            y += banner.Height + 8 * k;
        }

        // The map, which opens the canvas.
        // On a small screen the map gives way first, then the layouts' pictures.
        float mapHeight = Math.Max(48 * k, 114 * k - squeeze);
        float stillOver = Math.Max(0, squeeze - (114 * k - mapHeight));
        var hero = new RectangleF(m, y, w, mapHeight + 36 * k);
        Button(g, "open", hero, () => { if (s.Paused) actions.SetPaused(false); else actions.ToggleCanvas(); },
            s.Paused ? After.Stays : After.ClosesKeepingFocus);
        Map(g, new RectangleF(hero.X + 14 * k, hero.Y + 12 * k, hero.Width - 28 * k, mapHeight - 14 * k), s.Windows, s.Paused ? null : s.Viewport, s.Paused ? 0.4f : 1);
        Text(g, s.Paused ? "Resume Expanse" : s.Active ? "Back to 100%" : "Open Expanse", F(12, semibold: true), Primary, hero.X + inner, hero.Bottom - 27 * k, w - 140 * k);
        if (!s.Paused) Keys(g, ["Ctrl", "Alt", "`"], hero.Right - inner, hero.Bottom - 18 * k);
        y += hero.Height + 8 * k;

        // Centre and Tidy Up.
        float tileW = (w - 8 * k) / 2, tileH = 62 * k;
        void Tile(string key, float x, string icon, string title, string tip, Action run, Action<RectangleF> detail)
        {
            var r = new RectangleF(x, y, tileW, tileH);
            if (s.Paused) Fill(g, r, Radius, Color.FromArgb(CardFill.A / 2, CardFill), CardEdge);
            else Button(g, key, r, run, After.Closes, tip);
            Color ink = s.Paused ? Secondary : Primary;
            Text(g, icon, Icons(14), ink, r.X + inner, r.Y + 11 * k, 20 * k);
            Text(g, title, F(12, semibold: true), ink, r.X + 32 * k, r.Y + 9 * k, tileW - 40 * k);
            detail(r);
        }
        Tile("centre", m, "", "Centre window", "The window you're in, to the middle of the screen", actions.CentreWindow,
            r => Keys(g, ["Ctrl", "Alt", "C"], r.Right - inner, r.Bottom - 17 * k));
        Tile("tidy", m + tileW + 8 * k, "", "Tidy up", "Closes gaps and lines up edges, leaving each group where it is", actions.Tidy,
            r => Text(g, "Line up each group", F(11), Secondary, r.X + inner, r.Bottom - 25 * k, tileW - 2 * inner));
        y += tileH + 8 * k;

        // The switches.
        var switches = new RectangleF(m, y, w, 102 * k);
        Fill(g, switches, Radius, CardFill, CardEdge);
        SwitchRow(g, "autocentre", ref y, inner, "Auto-centre focused window", s.AutoCentre, actions.AutoCentre, ["Ctrl", "Alt", "Shift", "`"]);
        SwitchRow(g, "wallpaper", ref y, inner, "Desktop picture background", s.Wallpaper, actions.Wallpaper);
        SwitchRow(g, "names", ref y, inner, "Window names when zoomed out", s.Names, actions.Names);
        y += 8 * k;

        // The hot corner.
        var corner = new RectangleF(m, y, w, 74 * k);
        Fill(g, corner, Radius, CardFill, CardEdge);
        Text(g, "Hot corner", F(12, semibold: true), Primary, corner.X + inner, corner.Y + 7 * k, w - 110 * k);
        Text(g, "Rest the pointer there to open Expanse", F(11), Secondary, corner.X + inner, corner.Y + 24 * k, w - 100 * k);
        Pill(g, "corner-all", new RectangleF(corner.X + inner, corner.Y + 44 * k, 84 * k, 22 * k), "All corners", s.Corner == "all", () => actions.Corner("all"));
        Pill(g, "corner-off", new RectangleF(corner.X + inner + 90 * k, corner.Y + 44 * k, 48 * k, 22 * k), "Off", s.Corner == "off", () => actions.Corner("off"));
        var screen = new RectangleF(corner.Right - inner - 72 * k, corner.Y + 12 * k, 72 * k, 48 * k);
        Fill(g, screen, 4, Color.Transparent, Secondary);
        void Dot(string key, string name, float x, float yy)
        {
            bool on = s.Corner == key || s.Corner == "all";
            var r = new RectangleF(x - 8 * k, yy - 8 * k, 16 * k, 16 * k);
            hits.Add(new Hit("corner-" + key, r, () => actions.Corner(key), After.Stays, $"{name} corner"));
            using var brush = new SolidBrush(on ? accent : hover == "corner-" + key ? Primary : Secondary);
            float d = (on ? 10 : 7) * k;
            g.FillEllipse(brush, x - d / 2, yy - d / 2, d, d);
        }
        Dot("topLeft", "Top left", screen.X + 6 * k, screen.Y + 6 * k);
        Dot("topRight", "Top right", screen.Right - 6 * k, screen.Y + 6 * k);
        Dot("bottomLeft", "Bottom left", screen.X + 6 * k, screen.Bottom - 6 * k);
        Dot("bottomRight", "Bottom right", screen.Right - 6 * k, screen.Bottom - 6 * k);
        y += corner.Height + 8 * k;

        // Earlier layouts.
        float top = y;
        float cardInner = inner;
        y += 8 * k;
        float headY = y;
        y += 22 * k;
        float thumbW = (w - 2 * cardInner - 2 * 8 * k) / 3, thumbH = Math.Max(12 * k, 44 * k - stillOver);
        float noteH = 0;
        if (earlier.Count == 0)
        {
            noteH = g.MeasureString(EarlierNote, F(11), (int)(w - 2 * cardInner)).Height;
            y += noteH + 8 * k;
        }
        else y += thumbH + 38 * k;
        var card = new RectangleF(m, top, w, y - top);
        Fill(g, card, Radius, CardFill, CardEdge);
        Text(g, "Earlier layouts", F(12, semibold: true), Primary, card.X + cardInner, headY, 110 * k);
        float lx = card.Right - cardInner;
        void Link(string key, string title, string tip, Action run, After after)
        {
            float tw = g.MeasureString(title, F(11)).Width + 2 * k;
            lx -= tw;
            var r = new RectangleF(lx, headY, tw, 18 * k);
            hits.Add(new Hit(key, r, run, after, tip));
            Text(g, title, F(11), hover == key ? Primary : LinkInk, lx, headY + k, tw);
            lx -= 8 * k;
        }
        Link("restoreall", "Restore all windows", "Brings every window that's out of view back on screen", actions.RestoreAll, After.Closes);
        Link("snapshot", "Take snapshot", "Keeps the layout as it is now, to come back to", () => { actions.TakeSnapshot(); LoadEarlier(); }, After.Stays);
        if (earlier.Count == 0) Paragraph(g, EarlierNote, F(11), Secondary, card.X + cardInner, headY + 22 * k, w - 2 * cardInner, measuring);
        else
        {
            float tx = card.X + cardInner, ty = headY + 22 * k;
            foreach (var e in earlier)
            {
                var r = new RectangleF(tx, ty, thumbW, thumbH + 34 * k);
                var (ago, time) = When(e.Snapshot.Taken);
                Button(g, "layout-" + e.Snapshot.Path, r, () => actions.RestoreLayout(e.Snapshot), After.Closes,
                    $"Put the windows back where they were {ago.ToLowerInvariant()}", radius: 6);
                Map(g, new RectangleF(r.X + 6 * k, r.Y + 5 * k, r.Width - 12 * k, thumbH - 8 * k), e.Windows, null);
                Text(g, ago, F(10), Primary, r.X + 4 * k, r.Y + thumbH, r.Width - 8 * k, StringAlignment.Center);
                Text(g, time, F(10), Secondary, r.X + 4 * k, r.Y + thumbH + 14 * k, r.Width - 8 * k, StringAlignment.Center);
                tx += thumbW + 8 * k;
            }
        }
        y += 6 * k;

        // The footer: what's under the pointer, and the rest.
        float fy = y;
        string? tip = hits.FirstOrDefault(h => h.Key == hover)?.Tip;
        Text(g, tip ?? $"Expanse {Updater.CurrentVersion}", F(11), Secondary, m + 4 * k, fy + 9 * k, w - 4 * 34 * k - 8 * k);
        float bx = m + w;
        void Foot(string key, string glyph, string help, Action run, After after)
        {
            bx -= 32 * k;
            var r = new RectangleF(bx, fy, 30 * k, 30 * k);
            Button(g, key, r, run, after, help, radius: 6, plain: true);
            Text(g, glyph, Icons(14), Primary, r.X, r.Y + 7 * k, r.Width, StringAlignment.Center);
        }
        Foot("quit", "", "Quit Expanse", actions.Quit, After.Closes);
        Foot("settings", "", "Settings", () => { settingsPage = true; hover = null; }, After.Stays);
        Foot("troubleshooting", "", "Troubleshooting", () => Troubleshooting(s), After.Stays);
        Foot("welcome", "", "Welcome & Shortcuts", actions.Welcome, After.ClosesKeepingFocus);
        return y + 30 * k + m;
    }

    const string EarlierNote = "Expanse keeps your last few layouts, 15 minutes apart, to go back to. Take a snapshot to keep this one.";

    /// "12 minutes ago" and "9:29 AM" (or "Yesterday, 9:29 AM").
    static (string ago, string time) When(DateTime taken)
    {
        var ago = DateTime.Now - taken;
        static string N(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";
        string rel = ago.TotalMinutes < 1 ? "Just now" : ago.TotalHours < 1 ? N((int)ago.TotalMinutes, "min") : ago.TotalDays < 1 ? N((int)ago.TotalHours, "hour") : N((int)ago.TotalDays, "day");
        string at = taken.ToString("t");
        return (rel, taken.Date == DateTime.Today ? at : taken.Date == DateTime.Today.AddDays(-1) ? $"Yesterday, {at}" : taken.ToString("d"));
    }

    void Troubleshooting(State s)
    {
        GetCursorPos(out var p);
        menuOpen = true;
        Tray.Popup(hwnd, p.X, p.Y,
        [
            new("Detailed Logging", actions.DetailedLogging, s.DetailedLogging),
            new("Copy Logs for a Bug Report", actions.CopyLogs),
            new("Show Log File", actions.ShowLogFile, Enabled: s.HasLogFile),
            null,
            new("Report an Issue on GitHub…", actions.ReportIssue),
        ]);
        menuOpen = false;
        if (GetForegroundWindow() != hwnd) Close(restoreFocus: false);
    }

    // MARK: Settings

    float DrawSettings(Graphics g, State s, bool measuring, float squeeze)
    {
        float m = Margin * k, w = Width * k - 2 * m, y = m, inner = 12 * k;
        var back = new RectangleF(m, y, 30 * k, 30 * k);
        Button(g, "back", back, () => { settingsPage = false; hover = null; }, After.Stays, "Back", radius: 6, plain: true);
        Text(g, "", Icons(12), Primary, back.X, back.Y + 8 * k, back.Width, StringAlignment.Center);
        Text(g, "Settings", F(13, semibold: true), Primary, back.Right + 6 * k, y + 5 * k, w - 40 * k);
        y += 38 * k;

        var general = new RectangleF(m, y, w, 68 * k);
        Fill(g, general, Radius, CardFill, CardEdge);
        SwitchRow(g, "login", ref y, inner, "Open Expanse at login", s.OpenAtLogin, () => actions.OpenAtLogin(!s.OpenAtLogin));
        SwitchRow(g, "admin", ref y, inner, "Include admin windows", s.Admin, actions.AdminWindows, enabled: !s.Active);
        y += 8 * k;

        void Pick<T>(string key, string title, string note, (string name, T value)[] options, T selected, Action<T> pick)
        {
            float noteH = g.MeasureString(note, F(11), (int)(w - 2 * inner)).Height;
            float h = 8 * k + 20 * k + 26 * k + noteH + 14 * k;
            var card = new RectangleF(m, y, w, h);
            Fill(g, card, Radius, CardFill, CardEdge);
            Text(g, title, F(12, semibold: true), Primary, card.X + inner, y + 8 * k, w - 2 * inner);
            float px = card.X + inner, pw = (w - 2 * inner - 6 * k * (options.Length - 1)) / options.Length;
            foreach (var (name, value) in options)
            {
                Pill(g, $"{key}-{name}", new RectangleF(px, y + 30 * k, pw, 24 * k), name, EqualityComparer<T>.Default.Equals(value, selected), () => pick(value));
                px += pw + 6 * k;
            }
            Paragraph(g, note, F(11), Secondary, card.X + inner, y + 60 * k, w - 2 * inner, measuring);
            y += h + 8 * k;
        }
        Pick("gap", "Gaps between windows", "Snapping, Tidy up and the screens you build use it.",
            [("20", 20d), ("10", 10d), ("None", 0d)], s.Gap, actions.Gap);
        Pick("motion", "Glides and zooms", "Jump goes straight to where a glide or zoom would end. Match Windows jumps when its Animation effects are off.",
            [("Match Windows", "follow"), ("Animate", "glide"), ("Jump", "jump")], s.Motion, actions.Motion);

        const string devNote = "This is a development build: it has no updates.";
        float noteHeight = Updater.IsRelease ? 0 : g.MeasureString(devNote, F(11), (int)(w - 2 * inner)).Height + 6 * k;
        Fill(g, new RectangleF(m, y, w, (8 + 20 + 34 + 28) * k + noteHeight), Radius, CardFill, CardEdge);
        y += 8 * k;
        Text(g, "Updates", F(12, semibold: true), Primary, m + inner, y, w - 2 * inner);
        y += 20 * k;
        SwitchRow(g, "autoupdates", ref y, inner, "Check for updates automatically", s.AutoUpdates, () => actions.AutoUpdates(!s.AutoUpdates), enabled: Updater.IsRelease);
        float rowY = y;
        y += 28 * k;
        if (!Updater.IsRelease)
        {
            Paragraph(g, devNote, F(11), Secondary, m + inner, y, w - 2 * inner, measuring);
            y += noteHeight;
        }
        Text(g, $"Expanse {Updater.CurrentVersion}", F(12), Secondary, m + inner, rowY + 2 * k, 160 * k);
        const string check = "Check Now";
        float cw = g.MeasureString(check, F(11)).Width + 2 * k;
        var checkRect = new RectangleF(m + w - inner - cw, rowY + 2 * k, cw, 18 * k);
        hits.Add(new Hit("check", checkRect, actions.CheckForUpdates, After.Closes, null));
        Text(g, check, F(11), hover == "check" ? Primary : LinkInk, checkRect.X, checkRect.Y, cw);
        y += 8 * k;
        return y + m;
    }
}
