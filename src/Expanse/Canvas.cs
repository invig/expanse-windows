using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// The canvas: every window at a fixed spot, and a camera over them.
///
/// At 100% the windows are real: each sits where the camera puts it, which on Windows can be
/// wholly off-screen (no corner parking as on the Mac). Zoomed out, the overlay rises and shows
/// every window as a live DWM thumbnail, laid out by the camera. Clicking one animates the
/// camera to it at 100%, then the real windows move once, it gets focus and the overlay drops.
/// Timings and the camera maths follow the Mac app (CanvasController.swift).
sealed class Canvas
{
    readonly Overlay overlay;
    readonly Pictures pictures;
    readonly Wallpaper wallpaper = new();
    /// Bottom-most first: the last window is the frontmost, as in the Mac app.
    readonly List<CanvasWindow> windows = new();
    readonly List<Request> idleWaiters = new();
    Camera camera = Camera.Identity;
    Camera? returnCamera;
    RECT monitor, work;
    bool active;   // the overlay is up: zoomed out, or mid-glide
    bool gliding;  // up only to glide/dive into a window; input waits until it's handed back
    CanvasWindow? hovered;
    nint previousForeground;
    Animation? animation;
    Bench? bench;
    string lastAnimation = "", lastFocus = "", lastOverlayFocus = "";

    sealed class Animation
    {
        public Camera From, To;
        public long Start;
        public double Duration;
        public Action? Completion;
        public int Frames;
        public long FirstFrame, LastFrame;
        public double MaxGap, LayoutTotal;
    }

    public Canvas(Overlay overlay)
    {
        this.overlay = overlay;
        pictures = new Pictures(overlay.Hwnd);
        (monitor, work) = PrimaryMonitor();
        overlay.Painter = Paint;
        overlay.Handler = Handle;
        Log.Info($"screen {monitor}, work area {work}, dpi {GetDpiForWindow(overlay.Hwnd)}");
    }

    public bool IsAnimating => animation != null;
    Vec ViewSize => new(monitor.Width, monitor.Height);
    RectD View => new(0, 0, monitor.Width, monitor.Height);

    // MARK: Windows

    /// Brings the window list up to date. At 100%, a window found somewhere other than where we
    /// put it was moved by someone else, and its canvas spot follows it.
    public void Sync()
    {
        var found = CanvasWindow.Scan(); // topmost first
        var known = windows.ToDictionary(w => w.Hwnd);
        var next = new List<CanvasWindow>(found.Count);
        for (int i = found.Count - 1; i >= 0; i--)
        {
            nint h = found[i];
            if (!known.TryGetValue(h, out var w))
            {
                w = new CanvasWindow(h);
                w.Frame = ScreenToCanvas(w.ReadScreenFrame());
                Log.Info($"window {w} at {w.Frame}");
            }
            else
            {
                w.Title = Title(h);
                var f = w.ReadScreenFrame();
                if (!active)
                {
                    if (w.Placed is POINT p && p.X == f.Left && p.Y == f.Top)
                        w.Frame = w.Frame with { W = f.Width, H = f.Height };
                    else
                    {
                        var frame = ScreenToCanvas(f);
                        if (w.Placed != null) Log.Info($"window {w} moved by someone else: {w.Frame} -> {frame}");
                        w.Frame = frame;
                        w.Placed = null;
                    }
                }
            }
            next.Add(w);
        }
        foreach (var gone in windows.Except(next)) Log.Info($"window {gone} gone");
        windows.Clear();
        windows.AddRange(next);
    }

    /// At 100% (scale 1), canvas = screen - monitor origin + camera origin.
    RectD ScreenToCanvas(RECT f) =>
        new(f.Left - monitor.Left + camera.Origin.X, f.Top - monitor.Top + camera.Origin.Y, f.Width, f.Height);

    CanvasWindow? WindowAt(Vec viewPoint)
    {
        for (int i = windows.Count - 1; i >= 0; i--)
            if (camera.ToView(windows[i].Frame).Contains(viewPoint)) return windows[i];
        return null;
    }

    CanvasWindow? Find(string query)
    {
        if (query.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(query[2..], System.Globalization.NumberStyles.HexNumber, null, out long h))
            return windows.FirstOrDefault(w => w.Hwnd == (nint)h);
        return windows.LastOrDefault(w => w.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            ?? windows.LastOrDefault(w => w.App.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    void BringToFront(CanvasWindow w)
    {
        if (windows.Count > 0 && windows[^1] == w) return;
        windows.Remove(w);
        windows.Add(w);
        if (active) pictures.Raise(w);
    }

    // MARK: Zoom out, dive in

    public void Toggle()
    {
        if (gliding) return;
        if (active) Exit(null); else Enter(zoomOut: true);
    }

    /// Raises the overlay with every picture exactly over its real window, then (zoomOut)
    /// animates out to every window.
    public void Enter(bool zoomOut)
    {
        if (active)
        {
            if (zoomOut && !gliding) Animate(OverviewCamera(), 0.22);
            return;
        }
        (monitor, work) = PrimaryMonitor();
        Sync();
        previousForeground = GetForegroundWindow();
        active = true;
        gliding = !zoomOut;
        hovered = null;
        camera.Scale = 1;
        pictures.Attach(windows);
        pictures.Layout(windows, camera, View);
        overlay.Show(monitor, activate: zoomOut);
        Lift();
        if (zoomOut)
        {
            lastOverlayFocus = Focus(overlay.Hwnd);
            Log.Info($"zoom out: {windows.Count} windows; overlay foreground {lastOverlayFocus}");
            returnCamera = camera;
            Animate(OverviewCamera(), 0.22);
        }
    }

    /// GDI-drawn apps (classic Win32, Explorer's file list) only paint the parts of a window
    /// that are on a screen, so a window parked off-screen keeps a stale picture, or none if it
    /// was never on screen. While the overlay covers the screen, every window comes onto it,
    /// underneath, and paints; the hand-back puts each where the camera says.
    void Lift()
    {
        int lifted = 0;
        bool newOriginals = false;
        foreach (var w in windows)
        {
            GetWindowRect(w.Hwnd, out var r);
            int fx = r.Left + w.Insets.Left, fy = r.Top + w.Insets.Top;
            int fw = r.Width - w.Insets.Left - w.Insets.Right, fh = r.Height - w.Insets.Top - w.Insets.Bottom;
            int x = Math.Clamp(fx, monitor.Left, Math.Max(monitor.Left, monitor.Right - fw));
            int y = Math.Clamp(fy, monitor.Top, Math.Max(monitor.Top, monitor.Bottom - fh));
            if (x == fx && y == fy) continue;
            if (w.Original == null) { w.Original = r; newOriginals = true; }
            SetWindowPos(w.Hwnd, 0, x - w.Insets.Left, y - w.Insets.Top, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            lifted++;
        }
        if (newOriginals) Originals.Save(windows);
        if (lifted > 0) Log.Info($"lifted {lifted} off-screen windows under the overlay");
    }

    /// Back to 100%: on `focus` (centred in the work area), or where you were.
    public void Exit(CanvasWindow? focus, double duration = 0.18)
    {
        if (!active || (gliding && focus == null)) return;
        Camera target;
        if (focus != null)
        {
            BringToFront(focus);
            target = FocusCamera(focus);
        }
        else target = returnCamera ?? Camera.Centered(camera.Center(ViewSize), 1, ViewSize);
        gliding = true;
        hovered = null;
        Log.Info(focus != null ? $"dive into {focus}" : "back to 100%");
        Animate(target, duration, () => HandBack(focus));
    }

    /// From 100%: the overlay rises at scale 1 (pictures over the real windows), the camera
    /// glides to `w`, then everything is handed back as after a dive.
    public void Glide(CanvasWindow w)
    {
        if (gliding) return;
        if (active) { Exit(w); return; }
        Enter(zoomOut: false);
        Exit(w, 0.3);
    }

    /// Moves the real windows to where the camera says, waits for the ones in view to land,
    /// focuses, then drops the overlay.
    void HandBack(CanvasWindow? focus)
    {
        var t0 = Stopwatch.GetTimestamp();
        int moved = Commit();
        int waited = WaitForMoves();
        var target = focus ?? windows.FirstOrDefault(w => w.Hwnd == previousForeground)
            ?? windows.LastOrDefault(w => camera.ToView(w.Frame).Intersects(View));
        if (target != null && IsWindow(target.Hwnd))
        {
            lastFocus = $"{target}: {Focus(target.Hwnd)}";
            Log.Info($"focus {lastFocus}");
        }
        overlay.Hide();
        pictures.Detach();
        active = false;
        gliding = false;
        returnCamera = null;
        Log.Info($"handed back: moved {moved} windows, waited {waited} frames, {Ms(t0):0}ms");
        CompleteIdleWaiters();
    }

    /// Moves every window to its camera spot (asynchronously: a hung app can't stall us).
    /// Windows can sit fully off-screen; the window manager's limit is about ±32k.
    int Commit()
    {
        int moved = 0;
        bool newOriginals = false;
        foreach (var w in windows)
        {
            var v = camera.ToView(w.Frame);
            int x = (int)Math.Round(v.X) + monitor.Left - w.Insets.Left;
            int y = (int)Math.Round(v.Y) + monitor.Top - w.Insets.Top;
            x = Math.Clamp(x, -30000, 30000);
            y = Math.Clamp(y, -30000, 30000);
            w.Placed = new POINT(x + w.Insets.Left, y + w.Insets.Top);
            GetWindowRect(w.Hwnd, out var current);
            if (current.Left == x && current.Top == y) continue;
            if (w.Original == null) { w.Original = current; newOriginals = true; }
            SetWindowPos(w.Hwnd, 0, x, y, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            moved++;
        }
        if (newOriginals) Originals.Save(windows);
        return moved;
    }

    /// Up to ~10 frames for the windows that will be in view to reach their spots, so the
    /// overlay doesn't drop on a window still on its way.
    int WaitForMoves()
    {
        var inView = windows.Where(w => camera.ToView(w.Frame).Intersects(View) && w.Placed != null).ToList();
        for (int frame = 0; frame < 10; frame++)
        {
            bool landed = inView.All(w =>
            {
                var f = Dwm.FrameBounds(w.Hwnd);
                return !IsWindow(w.Hwnd) || (f.Left == w.Placed!.Value.X && f.Top == w.Placed.Value.Y);
            });
            if (landed) return frame;
            Dwm.DwmFlush();
        }
        Log.Info("hand-back: windows still moving after 10 frames");
        return 10;
    }

    /// Makes `hwnd` the foreground window. Windows only lets the foreground process (or the
    /// one that got the last input) do that: a hotkey or a click on the overlay qualifies. If
    /// it's refused, a dummy Alt tap (which makes us the last input's process), then
    /// AttachThreadInput. Returns which step worked, for the log and the spike write-up.
    static string Focus(nint hwnd)
    {
        nint before = GetForegroundWindow();
        string from = before == 0 ? "nothing" : $"\"{Title(before)}\"";
        bool returned = SetForegroundWindow(hwnd);
        if (IsForeground(hwnd)) return $"direct (from {from})";
        var steps = $"direct refused (returned {returned}, from {from})";
        TapAlt();
        returned = SetForegroundWindow(hwnd);
        if (IsForeground(hwnd)) return steps + ", ok after Alt tap";
        steps += $", Alt tap refused (returned {returned})";
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint me = Kernel32.GetCurrentThreadId();
        bool attached = fg != me && AttachThreadInput(me, fg, true);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        if (attached) AttachThreadInput(me, fg, false);
        return steps + (IsForeground(hwnd) ? ", ok with AttachThreadInput" : ", AttachThreadInput refused");
    }

    /// The switch can take a moment to show up for another thread's window.
    static bool IsForeground(nint hwnd)
    {
        for (int i = 0; i < 5; i++)
        {
            if (GetForegroundWindow() == hwnd) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    static void TapAlt()
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = VK_MENU;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = VK_MENU;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    // MARK: Camera targets

    /// `w` at 100%, centred in the work area (below the top if it's taller).
    Camera FocusCamera(CanvasWindow w)
    {
        double ax = work.Left - monitor.Left, ay = work.Top - monitor.Top;
        double x = ax + (work.Width - w.Frame.W) / 2;
        double y = w.Frame.H > work.Height ? ay : ay + (work.Height - w.Frame.H) / 2;
        return new Camera(new Vec(Math.Round(w.Frame.X - x), Math.Round(w.Frame.Y - y)), 1);
    }

    Camera OverviewCamera() => FitCamera(0.6);

    Camera FitCamera(double maxScale)
    {
        if (windows.Count == 0) return Camera.Centered(camera.Center(ViewSize), 0.5, ViewSize);
        var bounds = windows.Skip(1).Aggregate(windows[0].Frame, (b, w) => b.Union(w.Frame)).Inset(-80, -80);
        double s = Math.Min(Math.Min(ViewSize.X / bounds.W, ViewSize.Y / bounds.H), maxScale);
        return Camera.Centered(bounds.Center, Math.Max(s, Camera.MinScale), ViewSize);
    }

    // MARK: Animation

    void Animate(Camera to, double duration, Action? completion = null)
    {
        animation = new Animation
        {
            From = camera, To = to, Start = Stopwatch.GetTimestamp(), Duration = duration, Completion = completion,
        };
        overlay.Repaint(); // labels and the hover ring off while moving
    }

    /// One frame: called by the message loop, which then waits for DWM's next composition.
    public void Tick()
    {
        var a = animation;
        if (a == null) return;
        long now = Stopwatch.GetTimestamp();
        if (a.Frames == 0) a.FirstFrame = now;
        else
        {
            double gap = Stopwatch.GetElapsedTime(a.LastFrame, now).TotalMilliseconds;
            a.MaxGap = Math.Max(a.MaxGap, gap);
            bench?.Gaps.Add(gap);
        }
        a.Frames++;
        a.LastFrame = now;
        double t = Math.Min(1, Stopwatch.GetElapsedTime(a.Start, now).TotalSeconds / a.Duration);
        double e = 1 - Math.Pow(1 - t, 3); // ease-out cubic
        double s = Math.Exp(Lerp(Math.Log(a.From.Scale), Math.Log(a.To.Scale), e));
        var c0 = a.From.Center(ViewSize);
        var c1 = a.To.Center(ViewSize);
        camera = t >= 1 ? a.To : Camera.Centered(new Vec(Lerp(c0.X, c1.X, e), Lerp(c0.Y, c1.Y, e)), s, ViewSize);
        long l0 = Stopwatch.GetTimestamp();
        pictures.Layout(windows, camera, View);
        double layout = Ms(l0);
        a.LayoutTotal += layout;
        bench?.Layouts.Add(layout);
        if (t < 1) return;

        lastAnimation = $"{a.Frames} frames in {Ms(a.Start):0}ms, first after {Stopwatch.GetElapsedTime(a.Start, a.FirstFrame).TotalMilliseconds:0}ms, " +
                        $"max gap {a.MaxGap:0}ms, layout {a.LayoutTotal / a.Frames:0.00}ms/frame";
        if (bench == null) Log.Info($"animation: {lastAnimation}");
        animation = null;
        a.Completion?.Invoke();
        if (animation == null)
        {
            if (active && !gliding) overlay.Invalidate();
            CompleteIdleWaiters();
        }
    }

    static double Lerp(double a, double b, double t) => a + (b - a) * t;
    static double Ms(long since) => Stopwatch.GetElapsedTime(since).TotalMilliseconds;

    // MARK: Input

    nint? Handle(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY:
                Log.Info("hotkey");
                Toggle();
                return 0;
            case WM_MOUSEMOVE:
                Hover(new Vec((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF)));
                return 0;
            case WM_LBUTTONDOWN:
            {
                var p = new Vec((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                if (active && !gliding && WindowAt(p) is CanvasWindow w)
                {
                    Log.Info($"click on {w}");
                    Exit(w);
                }
                return 0;
            }
            case WM_MOUSEWHEEL:
            {
                if (!active || gliding) return 0;
                var screen = new Vec((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                int delta = (short)((wParam >> 16) & 0xFFFF);
                animation = null;
                camera.Zoom(camera.Scale * Math.Pow(1.2, delta / 120.0), new Vec(screen.X - monitor.Left, screen.Y - monitor.Top));
                pictures.Layout(windows, camera, View);
                overlay.Invalidate();
                return 0;
            }
            case WM_KEYDOWN:
                if ((int)wParam == VK_ESCAPE) Exit(null);
                return 0;
            case WM_SYSCOMMAND when (wParam & 0xFFF0) == SC_KEYMENU:
                return 0; // Alt (ours, from the focus fallback, or the user's) mustn't enter menu mode

            case WM_DISPLAYCHANGE:
                (monitor, work) = PrimaryMonitor();
                Log.Info($"display changed: screen {monitor}, work area {work}");
                if (active) overlay.Show(monitor, activate: false);
                return null;
            case Overlay.WM_COMMAND_REQUEST:
            {
                var handle = GCHandle.FromIntPtr(wParam);
                var request = (Request)handle.Target!;
                handle.Free();
                Command(request);
                return 0;
            }
        }
        return null;
    }

    void Hover(Vec p)
    {
        if (!active || gliding || animation != null) return;
        var w = WindowAt(p);
        if (w == hovered) return;
        hovered = w;
        overlay.Invalidate();
    }

    // MARK: Drawing

    /// The backdrop, plus (zoomed out and still) a label above each window and a ring around
    /// the one under the pointer. Both sit outside the pictures, so DWM doesn't cover them.
    void Paint(nint hdc, int width, int height)
    {
        wallpaper.Draw(hdc, width, height);
        if (!active || gliding || animation != null) return;
        double k = GetDpiForWindow(overlay.Hwnd) / 96.0;
        nint font = Gdi32.CreateFont(-(int)(13 * k), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        nint pill = Gdi32.CreateSolidBrush(Gdi32.RGB(32, 32, 34));
        nint oldFont = Gdi32.SelectObject(hdc, font);
        nint oldPen = Gdi32.SelectObject(hdc, Gdi32.GetStockObject(Gdi32.NULL_PEN));
        nint oldBrush = Gdi32.SelectObject(hdc, pill);
        Gdi32.SetBkMode(hdc, Gdi32.TRANSPARENT);
        Gdi32.SetTextColor(hdc, Gdi32.RGB(240, 240, 240));
        for (int i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            var r = camera.ToView(w.Frame);
            if (r.W < 80 || !r.Intersects(View)) continue;
            // No label for a window whose top is under one in front: it would sit on that one's.
            var top = new Vec(r.X + r.W / 2, r.Y + 1);
            if (windows.Skip(i + 1).Any(f => camera.ToView(f.Frame).Contains(top))) continue;
            var measure = new RECT(0, 0, 4000, 100);
            DrawText(hdc, w.Title, -1, ref measure, DT_SINGLELINE | DT_NOPREFIX | DT_CALCRECT);
            int pad = (int)(10 * k), h = (int)(22 * k);
            int lw = (int)Math.Min(measure.Width + pad * 2, r.W);
            int lx = (int)(r.X + (r.W - lw) / 2), ly = (int)(r.Y - h - 6 * k);
            Gdi32.RoundRect(hdc, lx, ly, lx + lw, ly + h, h, h);
            var text = new RECT(lx + pad, ly, lx + lw - pad, ly + h);
            DrawText(hdc, w.Title, -1, ref text, DT_SINGLELINE | DT_VCENTER | DT_CENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
        }
        if (hovered != null)
        {
            var r = camera.ToView(hovered.Frame);
            int t = Math.Max(2, (int)(3 * k)), gap = Math.Max(1, (int)(2 * k));
            nint pen = Gdi32.CreatePen(0, t, Gdi32.RGB(76, 154, 255));
            Gdi32.SelectObject(hdc, pen);
            Gdi32.SelectObject(hdc, Gdi32.GetStockObject(Gdi32.NULL_BRUSH));
            int o = gap + t / 2;
            Gdi32.Rectangle(hdc, (int)r.X - o, (int)r.Y - o, (int)(r.Right + o + 1), (int)(r.Bottom + o + 1));
            Gdi32.SelectObject(hdc, Gdi32.GetStockObject(Gdi32.NULL_PEN));
            Gdi32.DeleteObject(pen);
        }
        Gdi32.SelectObject(hdc, oldBrush);
        Gdi32.SelectObject(hdc, oldPen);
        Gdi32.SelectObject(hdc, oldFont);
        Gdi32.DeleteObject(pill);
        Gdi32.DeleteObject(font);
    }

    // MARK: Commands (from expansectl, over a named pipe)

    void Command(Request request)
    {
        var parts = request.Text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string arg = parts.Length > 1 ? parts[1].Trim() : "";
        Log.Info($"command: {request.Text.Trim()}");
        switch (verb)
        {
            case "state": request.Reply(State()); break;
            case "log": request.Reply(Log.Recent(int.TryParse(arg, out int n) ? n : 60)); break;
            case "toggle": Toggle(); WhenIdle(request); break;
            case "zoomout": Enter(zoomOut: true); WhenIdle(request); break;
            case "back": Exit(null); WhenIdle(request); break;
            case "pick":
                if (Find(arg) is CanvasWindow w) { if (active) Exit(w); else { Sync(); Glide(w); } WhenIdle(request); }
                else request.Reply($"no window matches \"{arg}\"");
                break;
            case "view":
            {
                var v = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(double.Parse).ToArray();
                if (!active || v.Length != 3) { request.Reply("view X Y SCALE, while zoomed out"); break; }
                animation = null;
                camera = new Camera(new Vec(v[0], v[1]), v[2]);
                pictures.Layout(windows, camera, View);
                overlay.Repaint();
                request.Reply(State());
                break;
            }
            case "close":
                if (Find(arg) is CanvasWindow c) { PostMessage(c.Hwnd, WM_CLOSE, 0, 0); request.Reply($"closing {c}"); }
                else request.Reply($"no window matches \"{arg}\"");
                break;
            case "bench": StartBench(request, double.TryParse(arg, out double secs) ? secs : 5); break;
            case "quit":
                request.Reply("bye");
                Quit();
                break;
            default:
                request.Reply("commands: state, log [N], toggle, zoomout, back, pick <title|app|0xHWND>, close <same>, view X Y SCALE, bench [SECONDS], quit");
                break;
        }
    }

    void WhenIdle(Request request)
    {
        idleWaiters.Add(request);
        if (animation == null && !gliding) CompleteIdleWaiters();
    }

    void CompleteIdleWaiters()
    {
        if (animation != null || gliding) return;
        var state = State();
        foreach (var r in idleWaiters) r.Reply(state);
        idleWaiters.Clear();
    }

    string State()
    {
        nint fg = GetForegroundWindow();
        return JsonSerializer.Serialize(new
        {
            active,
            gliding,
            animating = animation != null,
            camera = new { x = camera.Origin.X, y = camera.Origin.Y, scale = camera.Scale },
            screen = monitor.ToString(),
            workArea = work.ToString(),
            foreground = new { hwnd = $"0x{fg:X}", title = Title(fg) },
            hovered = hovered?.ToString(),
            lastAnimation,
            lastFocus,
            lastOverlayFocus,
            windows = windows.Select(w => new
            {
                hwnd = $"0x{w.Hwnd:X}",
                app = w.App,
                title = w.Title,
                canvas = w.Frame.ToString(),
                view = camera.ToView(w.Frame).ToString(),
                screen = RectD.From(Dwm.FrameBounds(w.Hwnd)).ToString(),
            }),
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// Puts every window back where it was before Expanse moved it, then quits.
    public void Quit()
    {
        Originals.Restore(windows);
        PostQuitMessage(0);
    }

    // MARK: Bench (spike: frame pacing of DWM thumbnails while the camera moves)

    sealed class Bench
    {
        public required Request Request;
        public long Start, Deadline;
        public readonly List<double> Gaps = new(), Layouts = new();
        public TimeSpan Cpu;
        public TimeSpan? DwmCpu;
    }

    /// Zoomed out, swings the camera between the overview and 2.5x closer, one second each
    /// way, for `seconds`, and replies with frame timing.
    void StartBench(Request request, double seconds)
    {
        if (!active || gliding) { request.Reply("zoom out first"); return; }
        bench = new Bench
        {
            Request = request, Start = Stopwatch.GetTimestamp(),
            Deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency),
            Cpu = Process.GetCurrentProcess().TotalProcessorTime, DwmCpu = DwmCpuTime(),
        };
        var overview = OverviewCamera();
        var close = Camera.Centered(overview.Center(ViewSize), overview.Scale * 2.5, ViewSize);
        void Swing(bool outward)
        {
            if (Stopwatch.GetTimestamp() >= bench!.Deadline) { FinishBench(); return; }
            Animate(outward ? overview : close, 1.0, () => Swing(!outward));
        }
        Swing(false);
    }

    void FinishBench()
    {
        var b = bench!;
        bench = null;
        double wall = Ms(b.Start);
        var gaps = b.Gaps.Order().ToList();
        double P(double q) => gaps.Count == 0 ? 0 : gaps[Math.Min(gaps.Count - 1, (int)(q * gaps.Count))];
        var dwmCpu = DwmCpuTime();
        b.Request.Reply(JsonSerializer.Serialize(new
        {
            mode = "dwm-thumbnails",
            windows = pictures.Count,
            seconds = wall / 1000,
            frames = b.Gaps.Count + 1,
            fps = (b.Gaps.Count + 1) / (wall / 1000),
            gapMs = new { p50 = P(0.5), p95 = P(0.95), p99 = P(0.99), max = gaps.LastOrDefault() },
            layoutMs = new { avg = b.Layouts.DefaultIfEmpty().Average(), max = b.Layouts.DefaultIfEmpty().Max() },
            cpuPercentOneCore = (Process.GetCurrentProcess().TotalProcessorTime - b.Cpu).TotalMilliseconds / wall * 100,
            dwmCpuPercentOneCore = b.DwmCpu is TimeSpan d0 && dwmCpu is TimeSpan d1 ? (d1 - d0).TotalMilliseconds / wall * 100 : (double?)null,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    static TimeSpan? DwmCpuTime()
    {
        try { return Process.GetProcessesByName("dwm").FirstOrDefault()?.TotalProcessorTime; }
        catch (Exception) { return null; }
    }
}
