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
/// every window as a live picture from DWM (see IPictures), laid out by the camera. Clicking one animates the
/// camera to it at 100%, then the real windows move once, it gets focus and the overlay drops.
/// Timings and the camera maths follow the Mac app (CanvasController.swift).
sealed class Canvas
{
    readonly Overlay overlay;
    IPictures pictures;
    readonly Wallpaper wallpaper = new();
    readonly Settings settings = Settings.Load();
    readonly Tray tray;
    readonly WinEvents events;
    /// Bottom-most first: the last window is the frontmost, as in the Mac app.
    readonly List<CanvasWindow> windows = new();
    readonly List<Request> idleWaiters = new();
    Camera camera = Camera.Identity;
    Camera? returnCamera;
    RECT monitor, work;
    bool active;   // the overlay is up: zoomed out, or mid-glide
    bool gliding;  // up only to glide/dive into a window; input waits until it's handed back
    bool paused;   // windows put back; the canvas ignores events and the hotkey until resumed
    bool pendingSync; // windows came or went mid-glide: sync once it's handed back
    nint settlingFocus;
    nint lastFocusedWindow; // the canvas window you were last in
    (nint hwnd, long at) lastOwnFocus;
    CanvasWindow? hovered;
    CanvasWindow? glideTarget; // where the glide or dive in flight is heading
    Drag? drag;                // a picture being dragged while zoomed out
    List<Layout.GuideLine> guides = new(); // snapping guides, canvas space
    bool guidesOverPictures;   // drawn by the pictures (shared visuals), not under them
    bool pendingTidy;          // Tidy Up from 100%: once zoomed out
    bool initialSyncDone;
    /// Windows that became visible since the last sync (EVENT_OBJECT_SHOW): the new ones among
    /// them are placed in free space.
    readonly HashSet<nint> shownRecently = new();
    /// Spots of windows minimised, hidden or cloaked: they come back to them.
    readonly Dictionary<nint, RectD> departed = new();

    sealed class Drag(CanvasWindow window, Vec startPoint)
    {
        public readonly CanvasWindow Window = window;
        public readonly RectD StartFrame = window.Frame;
        public readonly Vec StartPoint = startPoint;
        public bool Moved;
    }
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
        /// Windows moving on the canvas at the same time (Tidy Up).
        public List<(CanvasWindow w, RectD from, RectD to)>? Moves;
    }

    public Canvas(Overlay overlay, string pictureKind)
    {
        this.overlay = overlay;
        pictures = Pictures.Create(overlay.Hwnd, pictureKind);
        (monitor, work) = PrimaryMonitor();
        overlay.Painter = Paint;
        overlay.Handler = Handle;
        Log.Info($"screen {monitor}, work area {work}, dpi {GetDpiForWindow(overlay.Hwnd)}");
        tray = new Tray(overlay.Hwnd);
        events = new WinEvents(OnWindowEvent);
        lastFocusedWindow = GetForegroundWindow();
    }

    public bool IsAnimating => animation != null;
    Vec ViewSize => new(monitor.Width, monitor.Height);
    RectD View => new(0, 0, monitor.Width, monitor.Height);

    // MARK: Windows

    /// Brings the window list up to date. At 100%, a window found somewhere other than where we
    /// put it was moved by someone else, and its canvas spot follows it. Returns the windows that
    /// came and went.
    public (List<CanvasWindow> added, List<CanvasWindow> removed) Sync()
    {
        var added = new List<CanvasWindow>();
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
                added.Add(w);
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
        var removed = windows.Except(next).ToList();
        foreach (var gone in removed)
        {
            // Still there, just minimised, hidden or cloaked: it'll come back to its spot.
            if (IsWindow(gone.Hwnd)) departed[gone.Hwnd] = gone.Frame;
            Log.Info($"window {gone} {(IsWindow(gone.Hwnd) ? "left the canvas for now" : "gone")}");
        }
        foreach (var h in departed.Keys.Where(h => !IsWindow(h)).ToList()) departed.Remove(h);
        windows.Clear();
        windows.AddRange(next);
        return (added, removed);
    }

    /// The first sync: windows already open stay where they are.
    public void Start()
    {
        Sync();
        initialSyncDone = true;
    }

    /// A window that wasn't on the canvas appeared. Back from minimise (or hidden, or another
    /// virtual desktop): to its old spot. New (just shown): into free space beside the window
    /// you were in (Layout.FreeSpot), and the view glides there if that's off screen, as on
    /// the Mac.
    void Arrive(CanvasWindow w)
    {
        if (departed.Remove(w.Hwnd, out var spot))
        {
            w.Frame = spot with { W = w.Frame.W, H = w.Frame.H };
            Log.Info($"{w} back at its spot {w.Frame}");
            if (!active) Place(w);
            return;
        }
        if (!initialSyncDone || !shownRecently.Contains(w.Hwnd)) return;
        var anchor = windows.FirstOrDefault(x => x.Hwnd == lastFocusedWindow && x != w);
        var visible = camera.ToCanvas(new RectD(work.Left - monitor.Left, work.Top - monitor.Top, work.Width, work.Height));
        var others = windows.Where(x => x != w).Select(x => x.Frame).ToList();
        var target = anchor?.Frame.Center ?? camera.Center(ViewSize);
        if (Layout.FreeSpot(w.Frame.Size, anchor?.Frame, others, target, visible, onScreenOnly: !settings.AutoCentre) is not Vec o) return;
        w.Frame = w.Frame with { X = o.X, Y = o.Y };
        Log.Info($"placed new window {w} at {o.X:0},{o.Y:0}{(anchor != null ? $" beside {anchor}" : "")}");
        if (active) return; // its picture goes there; the window follows at the hand-back
        if (visible.Contains(w.Frame)) Place(w);
        else Glide(w, sync: false); // a sync now would read it back from where the app opened it
    }

    /// Canvas = screen - monitor origin + the 100% camera's origin. Zoomed out, the real windows
    /// are still where the camera you zoomed out from put them.
    RectD ScreenToCanvas(RECT f)
    {
        var origin = active && returnCamera is Camera r ? r.Origin : camera.Origin;
        return new(f.Left - monitor.Left + origin.X, f.Top - monitor.Top + origin.Y, f.Width, f.Height);
    }

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

    // MARK: Live tracking

    const nint SyncTimer = 1, FocusTimer = 2;
    public const int HotkeyCanvas = 1, HotkeyLeft = 2, HotkeyRight = 3, HotkeyUp = 4, HotkeyDown = 5;

    /// A window event (see WinEvents). Nothing heavy happens here: comings and goings and moves
    /// are gathered into one sync 50 ms after the last of them, and focus acts where it settles
    /// 120 ms later (it often bounces, as on the Mac).
    void OnWindowEvent(uint ev, nint hwnd)
    {
        if (paused) return;
        bool known = windows.Any(w => w.Hwnd == hwnd);
        switch (ev)
        {
            case EVENT_SYSTEM_FOREGROUND:
                settlingFocus = hwnd;
                SetTimer(overlay.Hwnd, FocusTimer, 120, 0);
                break;
            case EVENT_OBJECT_NAMECHANGE:
                if (windows.FirstOrDefault(w => w.Hwnd == hwnd) is CanvasWindow named)
                {
                    named.Title = Title(hwnd);
                    if (active && !gliding && animation == null) overlay.Invalidate();
                }
                break;
            case EVENT_OBJECT_LOCATIONCHANGE:
            case EVENT_SYSTEM_MOVESIZEEND:
                // Zoomed out or gliding, windows move because we move them.
                if (known && !active) ScheduleSync();
                break;
            case EVENT_OBJECT_SHOW:
            case EVENT_OBJECT_UNCLOAKED:
            case EVENT_SYSTEM_MINIMIZEEND:
                if (GetAncestor(hwnd, GA_ROOT) != hwnd) break;
                if (ev == EVENT_OBJECT_SHOW) shownRecently.Add(hwnd);
                ScheduleSync();
                break;
            case EVENT_OBJECT_HIDE:
            case EVENT_OBJECT_DESTROY:
            case EVENT_OBJECT_CLOAKED:
            case EVENT_SYSTEM_MINIMIZESTART:
                if (known) ScheduleSync();
                break;
        }
    }

    void ScheduleSync() => SetTimer(overlay.Hwnd, SyncTimer, 50, 0);

    /// Sync, and zoomed out, pictures for windows that came and none for ones that went.
    void SyncLive()
    {
        if (paused) return;
        if (gliding) { pendingSync = true; return; }
        bool wasActive = active;
        var (added, removed) = Sync();
        foreach (var w in added) Arrive(w);
        shownRecently.Clear();
        // An arrival may have started a glide, whose pictures are already up.
        if (!wasActive || gliding || (added.Count == 0 && removed.Count == 0)) return;
        foreach (var w in removed)
        {
            pictures.Remove(w);
            if (hovered == w) hovered = null;
        }
        foreach (var w in added) pictures.Add(w);
        pictures.Layout(windows, camera, View);
        if (animation == null) overlay.Invalidate();
    }

    /// A window got focus at 100%. If it isn't fully on screen (picked from the taskbar or
    /// Alt-Tab while parked, say), glide to it: the Mac's auto-centre. Not when focus comes back
    /// to the window you were already in, from the taskbar, tray or Start menu: on the Mac,
    /// clicking the menu bar never took focus away in the first place.
    void FocusSettled(nint hwnd)
    {
        if (paused || active || animation != null || GetForegroundWindow() != hwnd) return;
        // Our own hand-back just focused it.
        if (lastOwnFocus.hwnd == hwnd && Stopwatch.GetElapsedTime(lastOwnFocus.at).TotalSeconds < 1) return;
        var w = windows.FirstOrDefault(x => x.Hwnd == hwnd);
        if (w == null)
        {
            Sync();
            w = windows.FirstOrDefault(x => x.Hwnd == hwnd);
            if (w == null) return;
        }
        bool returning = w.Hwnd == lastFocusedWindow;
        lastFocusedWindow = w.Hwnd;
        if (returning || !settings.AutoCentre) return;
        var r = camera.ToView(w.Frame);
        var screen = View.Inset(-1, -1);
        if (r.X >= screen.X && r.Y >= screen.Y && r.Right <= screen.Right && r.Bottom <= screen.Bottom) return;
        Log.Info($"focus went to {w}, not fully on screen: glide to it");
        Glide(w);
    }

    // MARK: Hops

    /// Ctrl+Alt+arrow (or an arrow, zoomed out): the nearest window that way (Layout.Nearest).
    /// At 100% the view glides to it and it gets focus; zoomed out the highlight moves and the
    /// view follows. Mid-glide, the glide re-aims from the window it was heading to.
    public void Navigate(Direction direction)
    {
        if (paused) return;
        if (gliding)
        {
            if (glideTarget != null && Nearest(direction, glideTarget.Frame) is CanvasWindow next)
            {
                Log.Info($"hop {direction} (chained) to {next}");
                BringToFront(next);
                glideTarget = next;
                Animate(FocusCamera(next), 0.3, () => HandBack(next));
            }
            return;
        }
        if (animation != null) return;
        var centre = camera.Center(ViewSize);
        RectD from;
        if (active) from = (hovered ?? windows.LastOrDefault())?.Frame ?? new RectD(centre.X, centre.Y, 0, 0);
        else if (windows.FirstOrDefault(w => w.Hwnd == GetForegroundWindow()) is CanvasWindow current) from = current.Frame;
        else from = new RectD(centre.X, centre.Y, 0, 0);
        if (Nearest(direction, from) is not CanvasWindow target) return;
        Log.Info($"hop {direction} to {target}");

        if (active)
        {
            hovered = target;
            Animate(Camera.Centered(target.Frame.Center, camera.Scale, ViewSize), 0.25);
            return;
        }
        // With auto-centre on, every hop centres its window: a hop that only moved focus to a
        // window already on screen looked like nothing happened (as on the Mac).
        var r = camera.ToView(target.Frame);
        if (!settings.AutoCentre && View.Inset(-1, -1).Contains(r))
        {
            BringToFront(target);
            lastFocus = $"{target}: {Focus(target.Hwnd)}";
            lastFocusedWindow = target.Hwnd;
        }
        else Glide(target);
    }

    CanvasWindow? Nearest(Direction direction, RectD from) =>
        Layout.Nearest(direction, from, windows.Select(w => w.Frame).ToList()) is int i ? windows[i] : null;

    // MARK: Tidy Up

    /// T, or the tray menu: each group of windows tidied where it is (Layout.TidyUp: overlaps
    /// pushed apart, edges that nearly line up lined up, gaps closed to the standard 20), shown
    /// zoomed out with every window gliding to its new spot. The real windows follow at the
    /// hand-back. (The Mac also caps window sizes at the screen here; not yet.)
    public void Tidy()
    {
        if (paused || gliding || windows.Count == 0) return;
        if (!active) { pendingTidy = true; Enter(zoomOut: true); return; }
        if (animation != null || drag != null) return;
        var start = windows.Select(w => w.Frame).ToArray();
        var frames = start.ToArray();
        Layout.TidyUp(frames);
        for (int i = 0; i < frames.Length; i++)
            frames[i] = frames[i] with { X = Layout.Round(frames[i].X), Y = Layout.Round(frames[i].Y) };
        var moves = windows.Select((w, i) => (w, start[i], frames[i])).Where(m => m.Item2 != m.Item3).ToList();
        Log.Info($"tidy: {moves.Count} of {windows.Count} windows move");
        for (int i = 0; i < frames.Length; i++) windows[i].Frame = frames[i];
        var target = FitCamera(1);
        for (int i = 0; i < frames.Length; i++) windows[i].Frame = start[i];
        Animate(target, 0.3, null, moves);
    }

    // MARK: Dragging

    /// A picture dragged zoomed out moves its window's spot, snapping to the other windows'
    /// edges, centres and the standard gap (Layout.Snap) unless Ctrl is held (⌘ on the Mac).
    /// A press that doesn't move 3 px is a click: dive into the window.
    void DragTo(Vec p, bool snap)
    {
        var d = drag!;
        if (!d.Moved && p.DistanceTo(d.StartPoint) < 3) return;
        d.Moved = true;
        var proposed = d.StartFrame.Offset(new Vec((p.X - d.StartPoint.X) / camera.Scale, (p.Y - d.StartPoint.Y) / camera.Scale));
        var (frame, lines) = snap
            ? Layout.Snap(proposed, windows.Where(w => w != d.Window).Select(w => w.Frame).ToList(), camera.Scale, camera.Center(ViewSize))
            : (proposed, new List<Layout.GuideLine>());
        d.Window.Frame = frame;
        pictures.Moved(d.Window, camera, View);
        ShowGuides(lines);
        hovered = d.Window;
        overlay.Invalidate();
    }

    void EndDrag(bool click)
    {
        if (drag is not Drag d) return;
        drag = null;
        ReleaseCapture();
        ShowGuides(new());
        if (d.Moved) Log.Info($"dragged {d.Window} to {d.Window.Frame}");
        else if (click) { Log.Info($"click on {d.Window}"); Exit(d.Window); }
        overlay.Invalidate();
    }

    void ShowGuides(List<Layout.GuideLine> lines)
    {
        guides = lines;
        // The Mac's 1 pt guides are 2 px on a Retina screen.
        double thickness = Math.Max(2, Math.Round(2 * GetDpiForWindow(overlay.Hwnd) / 96.0));
        guidesOverPictures = pictures.SetGuides(lines.Select(l => (camera.ToView(l.From), camera.ToView(l.To))).ToList(), thickness);
    }

    // MARK: Pause, tray menu

    /// Puts every window back where it was before Expanse moved it and stops following them.
    public void Pause()
    {
        if (paused || active) return;
        Originals.Restore(windows);
        paused = true;
        tray.SetTip("Expanse (paused)");
        Log.Info("paused");
    }

    /// Starts again from the windows as they are now, at 100%.
    public void Resume()
    {
        if (!paused) return;
        paused = false;
        windows.Clear();
        camera = Camera.Identity;
        Sync();
        tray.SetTip("Expanse");
        Log.Info("resumed");
    }

    IReadOnlyList<Tray.Item?> TrayMenu() => new Tray.Item?[]
    {
        new("Open Expanse\tCtrl+Alt+`", () => Toggle(), Enabled: !paused),
        new(paused ? "Resume Expanse" : "Pause Expanse", () => { if (paused) Resume(); else Pause(); }),
        null,
        new("Auto-Centre Focused Window", () => { settings.AutoCentre = !settings.AutoCentre; settings.Save(); }, settings.AutoCentre),
        new("Desktop Picture Background", () => { settings.Wallpaper = !settings.Wallpaper; settings.Save(); }, settings.Wallpaper),
        new("Tidy Up Windows", Tidy, Enabled: !paused),
        null,
        new("Quit Expanse", Quit),
    };

    // MARK: Zoom out, dive in

    public void Toggle()
    {
        if (gliding || paused) return;
        if (active) Exit(null); else Enter(zoomOut: true);
    }

    /// Raises the overlay with every picture exactly over its real window, then (zoomOut)
    /// animates out to every window.
    public void Enter(bool zoomOut, bool sync = true)
    {
        if (active)
        {
            if (zoomOut && !gliding) Animate(OverviewCamera(), 0.22);
            return;
        }
        (monitor, work) = PrimaryMonitor();
        if (sync) Sync();
        previousForeground = GetForegroundWindow();
        active = true;
        gliding = !zoomOut;
        hovered = null;
        camera.Scale = 1;
        if (!pictures.Attach(windows) && pictures is SharedVisualPictures)
        {
            Log.Info("pictures: shared visuals failed for every window; falling back to DWM thumbnails");
            pictures.Dispose();
            pictures = new ThumbnailPictures(overlay.Hwnd);
            pictures.Attach(windows);
        }
        pictures.Layout(windows, camera, View);
        pictures.Flush();
        overlay.Show(monitor, activate: zoomOut);
        Lift();
        if (zoomOut)
        {
            lastOverlayFocus = Focus(overlay.Hwnd);
            Log.Info($"zoom out: {windows.Count} windows; overlay foreground {lastOverlayFocus}");
            returnCamera = camera;
            // The pointer may already be over a window, with no move to tell us.
            Animate(OverviewCamera(), 0.22, () =>
            {
                GetCursorPos(out var p);
                hovered = WindowAt(new Vec(p.X - monitor.Left, p.Y - monitor.Top));
                if (pendingTidy) { pendingTidy = false; Tidy(); }
            });
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
        glideTarget = focus;
        Log.Info(focus != null ? $"dive into {focus}" : "back to 100%");
        Animate(target, duration, () => HandBack(focus));
    }

    /// From 100%: the overlay rises at scale 1 (pictures over the real windows), the camera
    /// glides to `w`, then everything is handed back as after a dive.
    public void Glide(CanvasWindow w, bool sync = true)
    {
        if (gliding) return;
        if (active) { Exit(w); return; }
        Enter(zoomOut: false, sync);
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
            lastOwnFocus = (target.Hwnd, Stopwatch.GetTimestamp());
            lastFocusedWindow = target.Hwnd;
            Log.Info($"focus {lastFocus}");
        }
        overlay.Hide();
        pictures.Detach();
        active = false;
        gliding = false;
        glideTarget = null;
        returnCamera = null;
        Log.Info($"handed back: moved {moved} windows, waited {waited} frames, {Ms(t0):0}ms");
        if (pendingSync) { pendingSync = false; SyncLive(); }
        CompleteIdleWaiters();
    }

    /// Moves every window to its camera spot (asynchronously: a hung app can't stall us).
    /// Windows can sit fully off-screen; the window manager's limit is about ±32k.
    int Commit()
    {
        int moved = windows.Count(w => Place(w, saveOriginals: false));
        Originals.Save(windows);
        return moved;
    }

    /// Moves one window to its camera spot. True if it had to move.
    bool Place(CanvasWindow w, bool saveOriginals = true)
    {
        var v = camera.ToView(w.Frame);
        int x = (int)Math.Round(v.X) + monitor.Left - w.Insets.Left;
        int y = (int)Math.Round(v.Y) + monitor.Top - w.Insets.Top;
        x = Math.Clamp(x, -30000, 30000);
        y = Math.Clamp(y, -30000, 30000);
        w.Placed = new POINT(x + w.Insets.Left, y + w.Insets.Top);
        GetWindowRect(w.Hwnd, out var current);
        if (current.Left == x && current.Top == y) return false;
        if (w.Original == null)
        {
            w.Original = current;
            if (saveOriginals) Originals.Save(windows);
        }
        SetWindowPos(w.Hwnd, 0, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        return true;
    }

    /// Up to ~10 frames for the windows that will be in view to reach their spots, so the
    /// overlay doesn't drop on a window still on its way.
    int WaitForMoves()
    {
        var inView = windows.Where(w => camera.ToView(w.Frame).Intersects(View) && w.Placed != null).ToList();
        bool Landed(CanvasWindow w)
        {
            var f = Dwm.FrameBounds(w.Hwnd);
            return !IsWindow(w.Hwnd) || (f.Left == w.Placed!.Value.X && f.Top == w.Placed.Value.Y);
        }
        for (int frame = 0; frame < 10; frame++)
        {
            if (inView.All(Landed)) return frame;
            Dwm.DwmFlush();
        }
        var late = inView.Where(w => !Landed(w)).Select(w => $"{w} at {Dwm.FrameBounds(w.Hwnd).ToRectD()}, not {w.Placed!.Value.X},{w.Placed.Value.Y}");
        Log.Info($"hand-back: still moving after 10 frames: {string.Join("; ", late)}");
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

    void Animate(Camera to, double duration, Action? completion = null, List<(CanvasWindow, RectD, RectD)>? moves = null)
    {
        animation = new Animation
        {
            From = camera, To = to, Start = Stopwatch.GetTimestamp(), Duration = duration, Completion = completion, Moves = moves,
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
        if (a.Moves != null)
            foreach (var (w, from, to) in a.Moves)
            {
                w.Frame = t >= 1 ? to : new RectD(Lerp(from.X, to.X, e), Lerp(from.Y, to.Y, e), to.W, to.H);
                pictures.Moved(w, camera, View);
            }
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
        if (msg == tray.TaskbarCreated) { tray.Add(); return 0; }
        switch (msg)
        {
            case WM_TIMER when wParam == SyncTimer:
                KillTimer(overlay.Hwnd, SyncTimer);
                SyncLive();
                return 0;
            case WM_TIMER when wParam == FocusTimer:
                KillTimer(overlay.Hwnd, FocusTimer);
                FocusSettled(settlingFocus);
                return 0;
            case Tray.WM_TRAY:
                tray.Handle(wParam, lParam, () => { if (paused) Resume(); else Toggle(); }, TrayMenu);
                // The menu needed our (hidden) window in front; give focus back to the window you
                // were in, unless the canvas just opened.
                if (!active && GetForegroundWindow() == overlay.Hwnd && IsWindow(lastFocusedWindow))
                    SetForegroundWindow(lastFocusedWindow);
                return 0;
            case WM_SETTINGCHANGE when lParam != 0 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet":
                tray.ThemeChanged();
                return null;
            case WM_HOTKEY:
                switch ((int)wParam)
                {
                    case HotkeyCanvas: Log.Info("hotkey"); Toggle(); break;
                    case HotkeyLeft: Navigate(Direction.Left); break;
                    case HotkeyRight: Navigate(Direction.Right); break;
                    case HotkeyUp: Navigate(Direction.Up); break;
                    case HotkeyDown: Navigate(Direction.Down); break;
                }
                return 0;
            case WM_MOUSEMOVE:
            {
                var p = new Vec((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                if (drag != null) DragTo(p, snap: ((int)wParam & MK_CONTROL) == 0);
                else Hover(p);
                return 0;
            }
            case WM_LBUTTONDOWN:
            {
                var p = new Vec((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                if (active && !gliding && animation == null && WindowAt(p) is CanvasWindow w)
                {
                    BringToFront(w);
                    drag = new Drag(w, p);
                    SetCapture(overlay.Hwnd);
                }
                return 0;
            }
            case WM_LBUTTONUP:
                EndDrag(click: true);
                return 0;
            case WM_CAPTURECHANGED:
                EndDrag(click: false);
                return 0;
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
                if (!active || gliding) return 0;
                switch ((int)wParam)
                {
                    case VK_ESCAPE: Exit(null); break;
                    case VK_RETURN when windows.Count > 0: Exit(hovered ?? windows[^1]); break;
                    case VK_T: Tidy(); break;
                    case VK_LEFT: Navigate(Direction.Left); break;
                    case VK_RIGHT: Navigate(Direction.Right); break;
                    case VK_UP: Navigate(Direction.Up); break;
                    case VK_DOWN: Navigate(Direction.Down); break;
                }
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
        wallpaper.Draw(hdc, width, height, settings.Wallpaper);
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
        if (!guidesOverPictures && guides.Count > 0)
        {
            // DWM thumbnails draw over us: guides go underneath, visible between the pictures.
            nint pink = Gdi32.CreateSolidBrush(Gdi32.RGB(255, 56, 94));
            int t = Math.Max(1, (int)k);
            foreach (var g in guides)
            {
                Vec a = camera.ToView(g.From), b = camera.ToView(g.To);
                var line = a.X == b.X
                    ? new RECT((int)a.X - t / 2, (int)Math.Min(a.Y, b.Y), (int)a.X - t / 2 + t, (int)Math.Max(a.Y, b.Y))
                    : new RECT((int)Math.Min(a.X, b.X), (int)a.Y - t / 2, (int)Math.Max(a.X, b.X), (int)a.Y - t / 2 + t);
                FillRect(hdc, ref line, pink);
            }
            Gdi32.DeleteObject(pink);
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
            case "pictures":
                if (active) { request.Reply("back to 100% first"); break; }
                pictures.Dispose();
                pictures = Pictures.Create(overlay.Hwnd, arg);
                request.Reply(pictures.Name);
                break;
            case "bench": StartBench(request, double.TryParse(arg, out double secs) ? secs : 5); break;
            case "hop" when Enum.TryParse<Direction>(arg, ignoreCase: true, out var d): Navigate(d); WhenIdle(request); break;
            case "tidy": Tidy(); WhenIdle(request); break;
            case "minimize" or "unminimize":
            {
                // A window off the canvas for now (minimised) is found among the departed.
                nint h = Find(arg)?.Hwnd ?? departed.Keys.FirstOrDefault(d => Title(d).Contains(arg, StringComparison.OrdinalIgnoreCase));
                if (h == 0) { request.Reply($"no window matches \"{arg}\""); break; }
                ShowWindow(h, verb == "minimize" ? SW_MINIMIZE : SW_RESTORE);
                request.Reply($"{verb}d {Title(h)}");
                break;
            }
            case "pause": Pause(); request.Reply(State()); break;
            case "resume": Resume(); request.Reply(State()); break;
            case "autocentre":
                settings.AutoCentre = arg != "off";
                settings.Save();
                request.Reply($"auto-centre {(settings.AutoCentre ? "on" : "off")}");
                break;
            case "quit":
                request.Reply("bye");
                Quit();
                break;
            default:
                request.Reply("commands: state, log [N], toggle, zoomout, back, pick <title|app|0xHWND>, close <same>, view X Y SCALE, pictures shared|dwm, hop left|right|up|down, tidy, minimize|unminimize <same>, pause, resume, autocentre on|off, bench [SECONDS], quit");
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
            pictures = pictures.Name,
            paused,
            guides = guides.Select(g => $"{g.From.X:0},{g.From.Y:0} -> {g.To.X:0},{g.To.Y:0}"),
            guidesOverPictures,
            settings = new { settings.AutoCentre, settings.Wallpaper },
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
                screen = Dwm.FrameBounds(w.Hwnd).ToRectD().ToString(),
            }),
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// Puts every window back where it was before Expanse moved it, then quits.
    public void Quit()
    {
        events.Dispose();
        tray.Dispose();
        if (!paused) Originals.Restore(windows);
        PostQuitMessage(0);
    }

    // MARK: Bench (frame pacing of the pictures while the camera moves)

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
            mode = pictures.Name,
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
