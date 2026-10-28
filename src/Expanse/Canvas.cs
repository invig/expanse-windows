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
    readonly DesktopInput desktopInput;
    /// Bottom-most first: the last window is the frontmost, as in the Mac app.
    readonly List<CanvasWindow> windows = new();
    readonly List<Request> idleWaiters = new();
    Camera camera = Camera.Identity;
    Camera? returnCamera;
    /// You moved the view at 100% yourself (dragged or scrolled it): the windows you can see are
    /// where you want them. Clicking one that's partly on screen focuses it there, and diving into
    /// one on screen keeps the view (the Mac's viewPlaced). Any glide or zoom clears it.
    bool viewPlaced;
    RECT monitor, work;
    bool active;   // the overlay is up: zoomed out, or mid-glide
    bool gliding;  // up only to glide/dive into a window; input waits until it's handed back
    bool paused;   // windows put back; the canvas ignores events and the hotkey until resumed
    bool pendingSync; // windows came or went mid-glide: sync once it's handed back
    nint lastFocusedWindow; // the canvas window you were last in
    (nint hwnd, long at) lastOwnFocus;
    CanvasWindow? hovered;
    CanvasWindow? glideTarget; // where the glide or dive in flight is heading
    Drag? drag;                // a press on the zoomed-out canvas, until the button comes up
    /// Windows picked zoomed out (Shift-click, a marquee, Ctrl+A): they drag and close together.
    readonly HashSet<CanvasWindow> selected = new();
    RectD? marquee;            // the box being dragged out, view space
    bool spaceHeld;            // Space down, zoomed out: dragging pans
    /// The origin of the 100% camera you zoomed out from: zoomed out, the real windows are still
    /// where it put them.
    Vec liveOrigin;
    readonly List<CanvasWindow> closing = new(); // asked to close a moment ago
    /// Dialogs that came and went in the last sync (for their pictures).
    readonly List<CanvasWindow> ownedArrived = new(), ownedLeft = new();
    List<Layout.GuideLine> guides = new(); // snapping guides, canvas space
    bool guidesOverPictures;   // drawn by the pictures (shared visuals), not under them
    bool pendingTidy;          // Tidy Up from 100%: once zoomed out
    bool initialSyncDone;
    /// Windows that became visible since the last sync (EVENT_OBJECT_SHOW): the new ones among
    /// them are placed in free space.
    readonly HashSet<nint> shownRecently = new();
    /// Windows minimised, hidden or cloaked: they come back to their spots.
    readonly Dictionary<nint, CanvasWindow> departed = new();
    /// The layout saved by the last run, and its entries no window has claimed yet: an app opened
    /// later that had windows last time gets their spots.
    SavedLayout? saved;
    List<SavedWindow> savedPool = new();
    string? lastSavedJson;
    /// When the newest snapshot was taken, and the layout as last saved (for a snapshot before
    /// restoring another).
    DateTime lastSnapshotAt = LayoutStore.Snapshots().FirstOrDefault()?.Taken ?? DateTime.MinValue;
    SavedLayout? lastLayout;
    SavedLayout? pendingRestore; // Restore Layout from 100%: once zoomed out
    /// While displays change, Windows moves windows about; until things settle those moves are put
    /// back, not followed (the Mac's suspendTracking).
    bool suspendTracking;
    bool screenResized;
    CanvasWindow? centreAfterResize; // the window you were in when the screen changed
    SizeD? screenOverride;           // `screen W H`: pretend the screen is that size (testing)
    /// The canvas is the main screen's, as on the Mac. Other monitors: windows that turn up there
    /// are left alone, and nothing parked may land on one (ParkingSpot).
    List<RECT> otherMonitors = new();
    readonly List<RECT> pretendMonitors = new(); // `monitor X Y W H`: another one, for testing
    readonly HashSet<nint> elsewhere = new();    // windows on other monitors, left alone
    /// The main screen's DPI now, and the DPI the canvas's pixels are for: when they differ, the
    /// scaling changed and the canvas is scaled to match (ScaleCanvas).
    uint dpi = PrimaryDpi(), canvasDpi = PrimaryDpi();
    /// Windows you resized by dragging an edge (Windows brackets that with MOVESIZESTART/END):
    /// their new size is the size you chose.
    readonly HashSet<nint> userSized = new();

    enum DragKind { Windows, Marquee, Pan, Minimap }
    readonly Minimap minimap = new();

    /// A press on the zoomed-out canvas: on a window (it moves, with the rest of the selection if
    /// it's in one), on empty canvas (a marquee), or with Space held (the canvas pans).
    sealed class Drag(DragKind what, Vec startPoint)
    {
        public readonly DragKind What = what;
        public readonly Vec StartPoint = startPoint;
        public Vec Last = startPoint;
        public CanvasWindow? Clicked;
        public List<(CanvasWindow w, RectD start)> Moving = new();
        /// A marquee keeps what was selected before it (with Shift).
        public HashSet<CanvasWindow> Base = new();
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
        ReadScreen();
        overlay.Painter = Paint;
        overlay.Handler = Handle;
        Log.Info($"screen {monitor}, work area {work}, dpi {GetDpiForWindow(overlay.Hwnd)}");
        tray = new Tray(overlay.Hwnd);
        events = new WinEvents(OnWindowEvent);
        desktopInput = new DesktopInput(overlay.Hwnd);
        lastFocusedWindow = GetForegroundWindow();
    }

    public bool IsAnimating => animation != null;

    /// The primary screen and its work area (or, with `screen W H`, a pretend smaller one at its
    /// top-left).
    void ReadScreen()
    {
        (monitor, work) = PrimaryMonitor();
        dpi = PrimaryDpi();
        otherMonitors = Monitors().Where(m => !m.primary).Select(m => m.monitor).Concat(pretendMonitors).ToList();
        if (screenOverride is SizeD o)
        {
            monitor = new RECT(monitor.Left, monitor.Top, monitor.Left + (int)o.W, monitor.Top + (int)o.H);
            work = new RECT(work.Left, work.Top, Math.Min(work.Right, monitor.Right), Math.Min(work.Bottom, monitor.Bottom));
        }
    }
    Vec ViewSize => new(monitor.Width, monitor.Height);
    RectD View => new(0, 0, monitor.Width, monitor.Height);

    // MARK: Windows

    /// Brings the window list up to date. At 100%, a window found somewhere other than where we
    /// put it was moved by someone else, and its canvas spot follows it. Returns the windows that
    /// came and went.
    public (List<CanvasWindow> added, List<CanvasWindow> removed) Sync()
    {
        var added = new List<CanvasWindow>();
        ownedArrived.Clear();
        ownedLeft.Clear();
        var admin = new List<nint>();
        var found = CanvasWindow.Scan(admin); // topmost first
        NoteAdminWindows(admin);
        var known = windows.ToDictionary(w => w.Hwnd);
        var next = new List<CanvasWindow>(found.Count);
        for (int i = found.Count - 1; i >= 0; i--)
        {
            nint h = found[i];
            if (!known.TryGetValue(h, out var w))
            {
                // A window that turns up on another monitor is left alone (the Mac takes on only
                // windows on its screen), unless it's one Expanse had: minimised, or parked by the
                // Expanse that handed over (saved by its handle). One off every screen is taken on:
                // left alone it would be out of reach.
                if (OnOtherMonitorOnly(Dwm.FrameBounds(h)) && !departed.ContainsKey(h) && !savedPool.Any(e => e.Hwnd == h))
                {
                    if (elsewhere.Add(h)) Log.Info($"window {CanvasWindow.Describe(h)} is on another monitor: left alone");
                    continue;
                }
                elsewhere.Remove(h);
                if (departed.TryGetValue(h, out var back))
                {
                    // Back from being minimised or hidden: the same window, with everything known
                    // about it (where it was before Expanse moved it, its home spot, the size you
                    // chose, maximised). Arrive puts it back at its spot.
                    w = back;
                    w.Title = Title(h);
                    var f = w.ReadScreenFrame();
                    w.Frame = w.Frame with { W = f.Width, H = f.Height };
                    w.Placed = null;
                    if (IsZoomed(h)) (w.Maximised, w.RestoredByUs) = (true, false);
                    Log.Info($"window {w} is back");
                    added.Add(w);
                    next.Add(w);
                    continue;
                }
                w = new CanvasWindow(h) { Maximised = IsZoomed(h) };
                w.Frame = ScreenToCanvas(w.ReadScreenFrame());
                w.Preferred = w.Frame.Size;
                Log.Info($"window {w} at {w.Frame}");
                added.Add(w);
            }
            else
            {
                w.Title = Title(h);
                var f = w.ReadScreenFrame();
                if (!active)
                {
                    bool zoomed = IsZoomed(h);
                    if (zoomed != w.Maximised && !w.RestoredByUs)
                    {
                        w.Maximised = zoomed;
                        w.NormalRect = null;
                        Log.Info($"window {w} {(zoomed ? "maximised" : "restored from maximised")}");
                    }
                    var size = new SizeD(f.Width, f.Height);
                    // A VM or remote-screen window: the size its app gives it is the size chosen.
                    if (w.SizesItself && !suspendTracking && size != w.Preferred) w.Preferred = size;
                    bool ours = w.LastSized is SizeD l && Math.Abs(l.W - size.W) < 2 && Math.Abs(l.H - size.H) < 2;
                    if (userSized.Contains(h) && !ours && !suspendTracking && size != w.Preferred)
                    {
                        Log.Info($"window {w}: you chose {size.W:0}x{size.H:0}");
                        w.Preferred = size;
                        w.LastSized = null;
                    }
                    bool atPlaced = w.Placed is POINT p && p.X == f.Left && p.Y == f.Top;
                    if (atPlaced) w.Frame = w.Frame with { W = f.Width, H = f.Height };
                    else if (suspendTracking)
                    {
                        // A display change: Windows moved it; it goes back where the canvas says.
                        w.Frame = w.Frame with { W = f.Width, H = f.Height };
                        w.Placed = null;
                    }
                    else if (OnOtherMonitorOnly(f))
                    {
                        // You moved it onto another monitor: it's yours there, off the canvas, until
                        // you bring it back. (Off every screen it's still ours: one parked off the
                        // main screen, nudged as Windows rescaled it.)
                        Log.Info($"window {w} moved to another monitor: off the canvas");
                        elsewhere.Add(h);
                        continue;
                    }
                    else
                    {
                        var frame = ScreenToCanvas(f);
                        if (w.Placed != null) Log.Info($"window {w} moved by someone else: {w.Frame} -> {frame}");
                        var before = w.Frame.Origin;
                        w.Frame = frame;
                        w.Placed = null;
                        NoteUserMove(w, before);
                    }
                }
            }
            next.Add(w);
        }
        var removed = windows.Except(next).ToList();
        foreach (var gone in removed)
        {
            // Still there, just minimised, hidden or cloaked: it'll come back to its spot.
            if (elsewhere.Contains(gone.Hwnd)) { }
            else if (IsWindow(gone.Hwnd)) departed[gone.Hwnd] = gone;
            if (!elsewhere.Contains(gone.Hwnd)) Log.Info($"window {gone} {(IsWindow(gone.Hwnd) ? "left the canvas for now" : "gone")}");
            ownedLeft.AddRange(gone.Owned);
            gone.Owned.Clear();
        }
        foreach (var h in departed.Keys.Where(h => !IsWindow(h)).ToList()) departed.Remove(h);
        windows.Clear();
        windows.AddRange(next);
        SyncOwned();
        return (added, removed);
    }

    /// Dialogs and other owned windows, onto their owners (see CanvasWindow.Owner). At 100% each
    /// is read from the screen unless it and its owner are where Expanse put them: you or the app
    /// moved it, or its owner (which Windows doesn't move it with). Zoomed out they keep their
    /// place by their owner.
    void SyncOwned()
    {
        var byHwnd = windows.ToDictionary(w => w.Hwnd);
        var found = new Dictionary<CanvasWindow, List<CanvasWindow>>();
        foreach (var (h, root) in CanvasWindow.ScanOwned()) // topmost first
        {
            if (!byHwnd.TryGetValue(root, out var owner)) continue;
            var o = owner.Owned.FirstOrDefault(x => x.Hwnd == h);
            if (o == null)
            {
                o = new CanvasWindow(h) { Owner = owner };
                o.Frame = ScreenToCanvas(o.ReadScreenFrame());
                Log.Info($"dialog {o} of {owner} at {o.Frame}");
                KeepWithOwner(o);
                ownedArrived.Add(o);
            }
            else
            {
                o.Title = Title(h);
                var f = o.ReadScreenFrame();
                bool atPlaced = o.Placed is POINT p && p.X == f.Left && p.Y == f.Top;
                if (!active && suspendTracking) o.Placed = null; // a display change: it goes back by its owner
                if (!active && !suspendTracking && (!atPlaced || owner.Placed == null))
                {
                    o.Frame = ScreenToCanvas(f);
                    o.Placed = null;
                }
                else o.Frame = o.Frame with { W = f.Width, H = f.Height };
            }
            if (!found.TryGetValue(owner, out var list)) found[owner] = list = new();
            list.Add(o);
        }
        foreach (var w in windows)
        {
            var now = found.TryGetValue(w, out var list) ? Enumerable.Reverse(list).ToList() : new();
            foreach (var gone in w.Owned.Except(now))
            {
                Log.Info($"dialog {gone} closed");
                ownedLeft.Add(gone);
            }
            w.Owned.Clear();
            w.Owned.AddRange(now);
        }
    }

    /// A new dialog nowhere near its owner, while the owner is off screen (or zoomed out,
    /// anywhere): the app put it where you'd see it (Windows centres a message box on the screen
    /// when its owner isn't on one), but it belongs with its owner, so it goes in the middle of
    /// it. Focus on it then glides there with its owner (FocusSettled).
    void KeepWithOwner(CanvasWindow o)
    {
        var owner = o.Owner!;
        if (o.Frame.Intersects(owner.Frame)) return;
        if (!active && View.Contains(camera.ToView(owner.Frame))) return;
        o.Frame = o.Frame with { X = Layout.Round(owner.Frame.MidX - o.Frame.W / 2), Y = Layout.Round(owner.Frame.MidY - o.Frame.H / 2) };
        Log.Info($"dialog {o} opened away from {owner}: moved onto it, {o.Frame}");
        if (!active) Place(o);
    }

    /// The canvas window `hwnd` is, or the one it belongs to (a dialog's owner).
    CanvasWindow? CanvasWindowOf(nint hwnd)
    {
        if (hwnd == 0) return null;
        nint root = CanvasWindow.RootOwner(hwnd);
        return windows.FirstOrDefault(w => w.Hwnd == hwnd) ?? windows.FirstOrDefault(w => w.Hwnd == root);
    }

    /// `w`'s canvas frame changed: its picture, and its dialogs', follow.
    void PictureMoved(CanvasWindow w)
    {
        foreach (var x in w.WithOwned()) pictures.Moved(x, camera, View);
    }

    /// Every picture, bottom-most first: each window, then its dialogs.
    List<CanvasWindow> Stack() => windows.SelectMany(w => w.WithOwned()).ToList();

    /// The first sync: windows already open stay where they are.
    /// The first sync: the layout saved last time comes back (Mac: the first window scan). The
    /// camera first, so windows it doesn't know keep their real position; then each window it
    /// does goes back to its spot.
    public void Start()
    {
        SetTimer(overlay.Hwnd, HotCornerTimer, 33, 0); // the hot corner's poll, 30 times a second
        ReadScreen();
        saved = LayoutStore.Load();
        if (saved != null) LayoutStore.ToDpi(saved, dpi);
        canvasDpi = dpi;
        savedPool = saved?.Windows.ToList() ?? new();
        if (saved != null)
        {
            camera = Camera.Centered(new Vec(saved.CenterX, saved.CenterY), 1, ViewSize);
            camera.Origin = new Vec(Layout.Round(camera.Origin.X), Layout.Round(camera.Origin.Y));
        }
        Sync();
        if (saved?.HandOver == true) Originals.Adopt(windows);
        var open = OpenKeys();
        int restored = windows.Count(w => TakeSavedSpot(w, open));
        // Saved on another screen, perhaps: fitted to this one from the home layout.
        if (restored > 0) FitLayoutToScreen(ScreenMargin, tidy: false);
        int moved = Commit();
        Log.Info(saved == null ? "no saved layout" : $"saved layout: {restored} of {windows.Count} windows back at their spots ({moved} moved)");
        initialSyncDone = true;
        Save();
    }

    HashSet<(long, uint)> OpenKeys() =>
        windows.Concat(departed.Values).Select(w => ((long)w.Hwnd, w.Pid)).ToHashSet();

    /// Its saved spot, if the last run had one for it (see LayoutStore.TakeMatch).
    bool TakeSavedSpot(CanvasWindow w, ISet<(long, uint)> open)
    {
        if (LayoutStore.TakeMatch(w, open, savedPool) is not SavedWindow e) return false;
        w.Frame = w.Frame with { X = e.X, Y = e.Y };
        if (e.PW is double pw && e.PH is double ph && pw > 0) w.Preferred = new SizeD(pw, ph);
        if (e.HX is double hx && e.HY is double hy) w.Home = new Vec(hx, hy);
        return true;
    }

    // MARK: Saving

    const nint SaveTimer = 3;

    /// A second after the last change, as on the Mac.
    void ScheduleSave()
    {
        if (initialSyncDone && !paused) SetTimer(overlay.Hwnd, SaveTimer, 1000, 0);
    }

    /// `cleanExit`: windows are about to be put back where they were (quit, pause). `handOver`:
    /// they're staying put for the next Expanse (HandOver).
    void Save(bool cleanExit = false, bool handOver = false)
    {
        if (paused || !initialSyncDone) return;
        HomeFromCanvas();
        // Windows away (minimised, hidden) keep their spots too, and so do saved ones not open now.
        var layout = new SavedLayout
        {
            CenterX = camera.Center(ViewSize).X, CenterY = camera.Center(ViewSize).Y, CleanExit = cleanExit, HandOver = handOver,
            Dpi = canvasDpi,
            Windows = windows.Concat(departed.Values.Where(w => IsWindow(w.Hwnd))).Select(LayoutStore.Entry)
                .Concat(savedPool.Take(60)).ToList(),
        };
        string? before = lastSavedJson;
        lastSavedJson = LayoutStore.Save(layout, lastSavedJson);
        if (lastSavedJson != before && DateTime.Now - lastSnapshotAt >= LayoutStore.SnapshotSpacing) Snapshot(layout);
        lastLayout = layout;
    }

    void Snapshot(SavedLayout layout)
    {
        LayoutStore.SaveSnapshot(layout);
        lastSnapshotAt = DateTime.Now;
        Log.Info("layout snapshot taken");
    }

    // MARK: The home layout

    /// How far short of the size asked for a window can end up because its app rounds sizes
    /// (a terminal to whole character cells): that's still the chosen size, not one shrunk for a
    /// smaller screen. Shrinking to fit a screen takes far more.
    const double AppRounding = 32;
    /// The gap kept around a window that would otherwise fill the screen.
    const double ScreenMargin = 16;

    /// True while the canvas is a tightened version of the home layout: some window is smaller
    /// than its chosen size because this screen is smaller.
    bool IsDerived => windows.Any(w => !w.Maximised && w.Preferred.W > 0 &&
        (w.Frame.W < w.Preferred.W - AppRounding || w.Frame.H < w.Preferred.H - AppRounding));

    /// While no window is shrunk for a smaller screen, the canvas layout is the home layout. Not
    /// while the screen is changing (windows are still being shuffled).
    void HomeFromCanvas()
    {
        if (!IsDerived && !suspendTracking) foreach (var w in windows) w.Home = w.Frame.Origin;
    }

    /// You moved a window (dragging its picture, or for real). On the big screen its new spot is
    /// its home spot, straight away; on a smaller one the home spot moves by as much, so the move
    /// survives going back to the big screen.
    void NoteUserMove(CanvasWindow w, Vec before)
    {
        if (suspendTracking) return;
        if (!IsDerived) { w.Home = w.Frame.Origin; return; }
        var home = w.Home ?? before;
        w.Home = new Vec(home.X + w.Frame.X - before.X, home.Y + w.Frame.Y - before.Y);
    }

    /// Fit the layout to this screen without rebuilding it (the Mac's fitLayoutToScreen). Sizes:
    /// what you chose, capped at the screen. Positions: the home layout, tightened by however much
    /// windows shrank, so every gap keeps its size (Layout.Derive); on the big screen that's the
    /// home layout exactly. Only overlaps this screen caused are pushed apart. With `tidy`, the
    /// home layout itself is tidied first (Layout.TidyUp).
    void FitLayoutToScreen(double margin, bool tidy)
    {
        var max = new SizeD(work.Width - margin * 2, work.Height - margin * 2);
        var home = windows.Select(w =>
        {
            var o = w.Home ?? w.Frame.Origin;
            var size = w.Preferred.W > 0 && !w.Maximised ? w.Preferred : w.Frame.Size;
            return new RectD(o.X, o.Y, size.W, size.H);
        }).ToArray();
        if (tidy)
        {
            Layout.TidyUp(home);
            for (int i = 0; i < windows.Count; i++) windows[i].Home = new Vec(Layout.Round(home[i].X), Layout.Round(home[i].Y));
        }
        // Maximised windows are the size Windows gives them (see Place).
        foreach (var w in windows.Where(w => !w.Maximised))
        {
            var chosen = w.Preferred.W > 0 ? w.Preferred : w.Frame.Size;
            var target = new SizeD(Layout.Round(Math.Min(chosen.W, max.W)), Layout.Round(Math.Min(chosen.H, max.H)));
            if (Math.Abs(target.W - w.Frame.W) <= 1 && Math.Abs(target.H - w.Frame.H) <= 1) continue;
            var actual = ResizeReal(w, target);
            Log.Info($"fit size {w} {w.Frame.W:0}x{w.Frame.H:0} -> {actual.W:0}x{actual.H:0}");
            // Asked for the size you chose and the app came as close as it goes: that's the chosen
            // size from now on, rather than asking again at every fit.
            if (chosen.W <= max.W && chosen.H <= max.H &&
                Math.Abs(actual.W - target.W) <= AppRounding && Math.Abs(actual.H - target.H) <= AppRounding)
                w.Preferred = actual;
            w.Frame = w.Frame with { W = actual.W, H = actual.H };
        }
        // A little off its home size is an app rounding, not a smaller screen: counted as home,
        // or the windows beyond it would creep every time the screen changes.
        var sizes = windows.Select((w, i) =>
            Math.Abs(w.Frame.W - home[i].W) <= AppRounding && Math.Abs(w.Frame.H - home[i].H) <= AppRounding
                ? home[i].Size : w.Frame.Size).ToList();
        var origins = Layout.Derive(home, sizes);
        var frames = origins.Select((o, i) => new RectD(o.X, o.Y, windows[i].Frame.W, windows[i].Frame.H)).ToArray();
        // Windows you have overlapping in the home layout stay that way (pushing them apart would
        // push everything below and to the right too, and stick on the big screen).
        Layout.Separate(frames, Layout.Gap, onlyOverlaps: true, keep: Layout.OverlappingPairs(home));
        int moved = 0;
        for (int i = 0; i < windows.Count; i++)
        {
            var o = new Vec(Layout.Round(frames[i].X), Layout.Round(frames[i].Y));
            if (o.DistanceTo(windows[i].Frame.Origin) >= 1) moved++;
            windows[i].Frame = windows[i].Frame with { X = o.X, Y = o.Y };
        }
        Log.Info($"layout fitted: {moved} of {windows.Count} windows moved, derived={IsDerived}");
    }

    /// Shrinks a window that doesn't fit the screen, or grows it back toward the size you chose,
    /// before going to it (the Mac's fitToScreen).
    void FitToScreen(CanvasWindow w)
    {
        if (w.Preferred.W <= 0 || w.Maximised) return;
        var target = new SizeD(Layout.Round(Math.Min(w.Preferred.W, work.Width - ScreenMargin * 2)),
                               Layout.Round(Math.Min(w.Preferred.H, work.Height - ScreenMargin * 2)));
        if (Math.Abs(target.W - w.Frame.W) <= 1 && Math.Abs(target.H - w.Frame.H) <= 1) return;
        var actual = ResizeReal(w, target);
        Log.Info($"resize {w} {w.Frame.W:0}x{w.Frame.H:0} -> {actual.W:0}x{actual.H:0}");
        var c = w.Frame.Center;
        w.Frame = new RectD(c.X - actual.W / 2, c.Y - actual.H / 2, actual.W, actual.H);
    }

    /// Resizes the real window (its visible frame to `size`). Synchronous, to read back what the
    /// app accepted; a hung app is left alone.
    SizeD ResizeReal(CanvasWindow w, SizeD size)
    {
        if (IsHungAppWindow(w.Hwnd) || IsZoomed(w.Hwnd) || w.SizesItself) return w.Frame.Size;
        int width = (int)size.W + w.Insets.Left + w.Insets.Right, height = (int)size.H + w.Insets.Top + w.Insets.Bottom;
        SetWindowPos(w.Hwnd, 0, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE);
        var f = w.ReadScreenFrame();
        var actual = new SizeD(f.Width, f.Height);
        w.LastSized = actual;
        if (active) pictures.Resized(w);
        return actual;
    }

    // MARK: Display changes

    const nint RelayoutTimer = 4, FinalRelayoutTimer = 5, SettleTimer = 6;

    /// The screen changed (resolution, scale, taskbar): the window you're in is noted, then once
    /// Windows has finished moving windows about, everything is fitted to the new screen from the
    /// home layout, with that window in the middle. A second pass catches windows moved late;
    /// moves until things settle are put back, not followed. As on the Mac.
    void DisplaysChanging(int delayMs = 1000)
    {
        if (paused) return;
        centreAfterResize ??= CanvasWindowOf(GetForegroundWindow()) ?? CanvasWindowOf(lastFocusedWindow);
        suspendTracking = true;
        KillTimer(overlay.Hwnd, SettleTimer);
        SetTimer(overlay.Hwnd, RelayoutTimer, (uint)delayMs, 0);
        SetTimer(overlay.Hwnd, FinalRelayoutTimer, (uint)delayMs + 1500, 0);
    }

    void RelayoutForScreen(bool final)
    {
        if (paused) return;
        // Windows still moving (a glide): wait for them, or they'd land where the old screen wanted.
        if (gliding) { SetTimer(overlay.Hwnd, final ? FinalRelayoutTimer : RelayoutTimer, 500, 0); return; }
        var oldSize = ViewSize;
        ReadScreen();
        if (dpi != canvasDpi)
        {
            ScaleCanvas((double)dpi / canvasDpi);
            canvasDpi = dpi;
        }
        if (ViewSize != oldSize)
        {
            // Whatever was in the middle of the old screen stays in the middle of the new one.
            var centre = camera.Center(oldSize);
            camera = Camera.Centered(centre, camera.Scale, ViewSize);
            if (!active) camera.Origin = new Vec(Layout.Round(camera.Origin.X), Layout.Round(camera.Origin.Y));
            Log.Info($"display changed: {oldSize.X:0}x{oldSize.Y:0} -> {ViewSize.X:0}x{ViewSize.Y:0}, work area {work}");
            screenResized = true;
        }
        // Whatever Windows did to the real windows, every one is placed again.
        foreach (var w in windows) w.Placed = null;
        if (!active)
        {
            if (final) RestoreSizesAndPlace(); else Commit();
        }
        else if (final)
        {
            // Zoomed out, the layout still fits the new screen (the real windows follow on the way
            // out), but there's no window to centre.
            overlay.Show(monitor, activate: false);
            FitLayoutToScreen(ScreenMargin, tidy: false);
            foreach (var w in windows) PictureMoved(w);
            Relayout();
            screenResized = false;
            centreAfterResize = null;
        }
        if (final) SetTimer(overlay.Hwnd, SettleTimer, 10_000, 0);
    }

    /// Sizes back to what you chose (capped at this screen), the layout fitted from home; then,
    /// if the screen changed size, the window you were in goes in the middle.
    void RestoreSizesAndPlace()
    {
        FitLayoutToScreen(ScreenMargin, tidy: false);
        if (screenResized && (centreAfterResize is CanvasWindow c && windows.Contains(c) ? c
                : CanvasWindowOf(GetForegroundWindow())) is CanvasWindow w)
        {
            camera = FocusCamera(w);
            Log.Info($"display change: centred on {w}");
        }
        screenResized = false;
        centreAfterResize = null;
        int moved = Commit();
        Log.Info($"display change: placed {moved} windows");
        ScheduleSave();
    }

    /// A window that wasn't on the canvas appeared. Back from minimise (or hidden, or another
    /// virtual desktop): to its old spot. New (just shown): into free space beside the window
    /// you were in (Layout.FreeSpot), and the view glides there if that's off screen, as on
    /// the Mac.
    void Arrive(CanvasWindow w)
    {
        if (departed.Remove(w.Hwnd, out var away))
        {
            w.Frame = away.Frame with { W = w.Frame.W, H = w.Frame.H };
            Log.Info($"{w} back at its spot {w.Frame}");
            if (!active) Place(w);
            return;
        }
        if (!initialSyncDone) return;
        if (TakeSavedSpot(w, OpenKeys()))
        {
            // An app opened now that had windows last time: quietly back to their spot (focus
            // decides whether the view goes there), as on the Mac.
            Log.Info($"{w} back at its saved spot {w.Frame}");
            if (!active) Place(w);
            return;
        }
        if (!shownRecently.Contains(w.Hwnd)) return;
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
        var origin = active ? liveOrigin : camera.Origin;
        return new(f.Left - monitor.Left + origin.X, f.Top - monitor.Top + origin.Y, f.Width, f.Height);
    }

    CanvasWindow? WindowAt(Vec viewPoint)
    {
        // A dialog's picture counts as its owner's.
        for (int i = windows.Count - 1; i >= 0; i--)
            if (windows[i].WithOwned().Any(w => camera.ToView(w.Frame).Contains(viewPoint))) return windows[i];
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
        if (active) foreach (var x in w.WithOwned()) pictures.Raise(x);
    }

    // MARK: Live tracking

    const nint SyncTimer = 1, FocusTimer = 2;
    public const int HotkeyCanvas = 1, HotkeyLeft = 2, HotkeyRight = 3, HotkeyUp = 4, HotkeyDown = 5,
        HotkeyCentre = 6, HotkeyAutoCentre = 7;

    /// A window event (see WinEvents). Nothing heavy happens here: comings and goings and moves
    /// are gathered into one sync 50 ms after the last of them, and focus acts where it settles
    /// 120 ms later (it often bounces, as on the Mac).
    void OnWindowEvent(uint ev, nint hwnd)
    {
        if (paused) return;
        bool known = windows.Any(w => w.Hwnd == hwnd || w.Owned.Any(o => o.Hwnd == hwnd));
        switch (ev)
        {
            case EVENT_SYSTEM_FOREGROUND:
                SetTimer(overlay.Hwnd, FocusTimer, 120, 0);
                break;
            case EVENT_OBJECT_NAMECHANGE:
                if (windows.FirstOrDefault(w => w.Hwnd == hwnd) is CanvasWindow named)
                {
                    named.Title = Title(hwnd);
                    if (active && !gliding) Redraw();
                }
                break;
            case EVENT_OBJECT_LOCATIONCHANGE:
            case EVENT_SYSTEM_MOVESIZEEND:
                // Zoomed out or gliding, windows move because we move them.
                if (known && !active)
                {
                    if (ev == EVENT_SYSTEM_MOVESIZEEND) userSized.Add(hwnd);
                    ScheduleSync();
                }
                // One on another monitor may be coming onto the main screen.
                else if (!active && elsewhere.Contains(hwnd)) ScheduleSync();
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
        userSized.Clear();
        if (suspendTracking && !active) Commit();
        ScheduleSave();
        // An arrival may have started a glide, whose pictures are already up.
        if (!wasActive || gliding || (added.Count == 0 && removed.Count == 0 && ownedArrived.Count == 0 && ownedLeft.Count == 0)) return;
        foreach (var w in removed)
        {
            pictures.Remove(w);
            if (hovered == w) hovered = null;
            selected.Remove(w);
        }
        foreach (var o in ownedLeft) pictures.Remove(o);
        foreach (var w in added) foreach (var x in w.WithOwned()) pictures.Add(x);
        // A dialog opening brings its window to the front, as it does at 100%.
        foreach (var o in ownedArrived.Where(o => !added.Contains(o.Owner!)))
        {
            pictures.Add(o);
            BringToFront(o.Owner!);
        }
        Relayout();
    }

    /// A window got focus at 100%. If it isn't fully on screen (picked from the taskbar or
    /// Alt-Tab while parked, say), glide to it: the Mac's auto-centre. Not when focus comes back
    /// to the window you were already in, from the taskbar, tray or Start menu: on the Mac,
    /// clicking the menu bar never took focus away in the first place.
    ///
    /// What counts is the window in front once focus has settled, not the last event's: a click
    /// on a taskbar button whose window has a dialog up reports the taskbar, then activates the
    /// dialog without an event of its own.
    void FocusSettled()
    {
        nint hwnd = GetForegroundWindow();
        if (paused || active || animation != null || hwnd == 0) return;
        // Our own hand-back just focused it.
        if (lastOwnFocus.hwnd == hwnd && Stopwatch.GetElapsedTime(lastOwnFocus.at).TotalSeconds < 1) return;
        var w = CanvasWindowOf(hwnd);
        if (w == null || (w.Hwnd != hwnd && w.Owned.All(o => o.Hwnd != hwnd)))
        {
            Sync();
            w = CanvasWindowOf(hwnd);
            if (w == null) return;
        }
        // Focus on a dialog: whether the dialog is in view is what counts, and its owner comes too.
        var focused = w.Owned.FirstOrDefault(o => o.Hwnd == hwnd) ?? w;
        bool returning = w.Hwnd == lastFocusedWindow;
        lastFocusedWindow = w.Hwnd;
        if (returning || !settings.AutoCentre) return;
        var r = camera.ToView(focused.Frame);
        var screen = View.Inset(-1, -1);
        if (r.X >= screen.X && r.Y >= screen.Y && r.Right <= screen.Right && r.Bottom <= screen.Bottom) return;
        if (viewPlaced && GoesOnScreen(r))
        {
            // Partly on screen, in a view you put there: it's the one you clicked, not one to go to.
            Log.Info($"view placed by you: {focused} focused where it is");
            return;
        }
        Log.Info($"focus went to {focused}, not fully on screen: glide to {w}");
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
        else if (CanvasWindowOf(GetForegroundWindow()) is CanvasWindow current) from = current.Frame;
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
        // Home spots are otherwise brought up to date when the layout is saved, which windows
        // you've only just moved may still be waiting for: Tidy would put them back.
        HomeFromCanvas();
        ShowRearranged(() => FitLayoutToScreen(ScreenMargin, tidy: true), "tidy");
    }

    /// Rearranges the canvas with `arrange` (which sets every window's frame), shown zoomed out:
    /// every window glides from where it was to its new spot while the camera fits them all.
    /// The real windows follow at the hand-back.
    void ShowRearranged(Action arrange, string what)
    {
        var start = windows.Select(w => w.Frame).ToArray();
        arrange();
        var end = windows.Select(w => w.Frame).ToArray();
        var moves = windows.Select((w, i) => (w, start[i], end[i])).Where(m => m.Item2 != m.Item3).ToList();
        Log.Info($"{what}: {moves.Count} of {windows.Count} windows move");
        var target = FitCamera(1);
        for (int i = 0; i < start.Length; i++) windows[i].Frame = start[i] with { W = end[i].W, H = end[i].H };
        Animate(target, 0.3, null, moves);
        ScheduleSave();
    }

    // MARK: Restore Layout

    /// Puts every window back where `layout` (a snapshot) had it, fitted to this screen (it may
    /// have been taken on another); windows it doesn't know stay where they are. The layout it
    /// replaces becomes a snapshot itself, so this can be undone. Shown zoomed out, like Tidy Up.
    public void RestoreLayout(SavedLayout layout)
    {
        if (paused || gliding) return;
        if (!active) { pendingRestore = layout; Enter(zoomOut: true); return; }
        if (animation != null || drag != null) return;
        Save();
        if (lastLayout != null) Snapshot(lastLayout);
        LayoutStore.ToDpi(layout, canvasDpi);
        var pool = layout.Windows.ToList();
        var open = OpenKeys();
        int restored = 0;
        ShowRearranged(() =>
        {
            foreach (var w in windows)
            {
                if (LayoutStore.TakeMatch(w, open, pool) is not SavedWindow e) continue;
                // Its home spot and chosen size; fitting works out where it goes on this screen.
                w.Home = new Vec(e.HX ?? e.X, e.HY ?? e.Y);
                w.Frame = w.Frame with { X = w.Home.Value.X, Y = w.Home.Value.Y };
                if ((e.PW ?? e.W) is double pw && (e.PH ?? e.H) is double ph && pw > 0) w.Preferred = new SizeD(pw, ph);
                restored++;
            }
            FitLayoutToScreen(ScreenMargin, tidy: false);
        }, "restore layout");
        Log.Info($"restored a layout: {restored} of {windows.Count} windows");
    }

    // MARK: The zoomed-out mouse

    /// As on the Mac: with Space held, dragging pans. On a window, its × closes it (or the
    /// selection it's in), Shift toggles it in the selection, and otherwise it drags (with the
    /// selection, if it's in one). On empty canvas a marquee selects what it touches, adding to
    /// the selection with Shift.
    void Press(Vec p, bool shift)
    {
        if (minimap.Panel.Contains(p))
        {
            // The minimap: the view goes where you click, and follows as you drag.
            drag = new Drag(DragKind.Minimap, p);
            minimap.Frozen = true;
            CentreOn(minimap.CanvasPoint(p));
        }
        else if (spaceHeld) drag = new Drag(DragKind.Pan, p);
        else if (WindowAt(p) is CanvasWindow w)
        {
            double k = UiScale;
            if (CloseShown(w, w, k) && Marks.CloseRect(camera.ToView(w.Frame), k).Contains(p))
            {
                Close(w);
                return;
            }
            if (shift)
            {
                if (!selected.Remove(w)) selected.Add(w);
                Redraw();
                return;
            }
            List<CanvasWindow> group = InGroup(w) ? windows.Where(selected.Contains).ToList() : [w];
            if (group.Count == 1) selected.Clear();
            foreach (var g in group) BringToFront(g);
            drag = new Drag(DragKind.Windows, p) { Clicked = w, Moving = group.Select(g => (g, g.Frame)).ToList() };
        }
        else
        {
            drag = new Drag(DragKind.Marquee, p) { Base = shift ? selected.ToHashSet() : new() };
            if (!shift) selected.Clear();
        }
        SetCapture(overlay.Hwnd);
        Redraw();
    }

    /// Dragged pictures move their windows' spots: one window, or the selection's bounding box as
    /// one block, snapping to the other windows' edges, centres and the standard gap (Layout.Snap)
    /// unless Ctrl is held (⌘ on the Mac). A press that doesn't move 3 px is a click: dive in.
    void DragTo(Vec p, bool snap)
    {
        var d = drag!;
        switch (d.What)
        {
            case DragKind.Minimap:
                d.Moved = true;
                CentreOn(minimap.CanvasPoint(p));
                return;
            case DragKind.Pan:
                d.Moved = true;
                PanBy(p - d.Last);
                d.Last = p;
                return;
            case DragKind.Marquee:
            {
                d.Moved = true;
                var box = new RectD(Math.Min(d.StartPoint.X, p.X), Math.Min(d.StartPoint.Y, p.Y),
                    Math.Abs(p.X - d.StartPoint.X), Math.Abs(p.Y - d.StartPoint.Y));
                marquee = box;
                selected.Clear();
                selected.UnionWith(d.Base);
                selected.UnionWith(windows.Where(w => camera.ToView(w.Frame).Intersects(box)));
                Redraw();
                return;
            }
        }
        if (!d.Moved && p.DistanceTo(d.StartPoint) < 3) return;
        d.Moved = true;
        var bounds = d.Moving.Skip(1).Aggregate(d.Moving[0].start, (b, m) => b.Union(m.start));
        var proposed = bounds.Offset(new Vec((p.X - d.StartPoint.X) / camera.Scale, (p.Y - d.StartPoint.Y) / camera.Scale));
        var moving = d.Moving.Select(m => m.w).ToHashSet();
        var (snapped, lines) = snap
            ? Layout.Snap(proposed, windows.Where(w => !moving.Contains(w)).Select(w => w.Frame).ToList(), camera.Scale, camera.Center(ViewSize))
            : (proposed, new List<Layout.GuideLine>());
        foreach (var (w, start) in d.Moving)
        {
            w.Frame = new RectD(snapped.X + (start.X - bounds.X), snapped.Y + (start.Y - bounds.Y), start.W, start.H);
            PictureMoved(w);
        }
        ShowGuides(lines);
        hovered = d.Clicked;
        Relayout();
    }

    void EndDrag(bool click)
    {
        if (drag is not Drag d) return;
        drag = null;
        ReleaseCapture();
        ShowGuides(new());
        marquee = null;
        minimap.Frozen = false;
        switch (d.What)
        {
            case DragKind.Windows when d.Moved:
                Log.Info(d.Moving.Count == 1 ? $"dragged {d.Clicked} to {d.Clicked!.Frame}"
                    : $"dragged {d.Moving.Count} windows by {d.Clicked!.Frame.X - d.Moving.First(m => m.w == d.Clicked).start.X:0},{d.Clicked.Frame.Y - d.Moving.First(m => m.w == d.Clicked).start.Y:0}");
                foreach (var (w, start) in d.Moving) NoteUserMove(w, start.Origin);
                ScheduleSave();
                break;
            case DragKind.Windows when click:
                Log.Info($"click on {d.Clicked}");
                selected.Clear();
                Exit(d.Clicked);
                break;
            case DragKind.Marquee when d.Moved:
                Log.Info($"marquee: {selected.Count} selected");
                break;
        }
        Redraw();
    }

    /// Part of a selection of more than one window (which × and Ctrl+W close together).
    bool InGroup(CanvasWindow? w) => w != null && selected.Count > 1 && selected.Contains(w);

    // MARK: Camera, by hand

    /// Wheel, pinch, Space-drag and the zoom keys. You moved the camera yourself, so Esc now goes
    /// back to 100% where you're looking, not where you were (the Mac's cameraMovedByUser).
    void PanBy(Vec d)
    {
        CancelAnimation();
        camera.Pan(d);
        returnCamera = null;
        viewPlaced = Math.Abs(camera.Scale - 1) < 0.001;
        if (drag == null) HoverAtCursor();
        Relayout();
    }

    void ZoomBy(double factor, Vec anchor)
    {
        CancelAnimation();
        camera.Zoom(camera.Scale * factor, anchor);
        returnCamera = null;
        viewPlaced = false;
        HoverAtCursor();
        Relayout();
    }

    /// The view's middle to canvas point `c`, at the same zoom (the minimap).
    void CentreOn(Vec c)
    {
        CancelAnimation();
        camera = Camera.Centered(c, camera.Scale, ViewSize);
        returnCamera = null;
        Relayout();
    }

    /// 1, - and =: to `scale` about the middle of the screen, animated. (0 fits every window.)
    void ZoomKey(double scale)
    {
        CancelAnimation();
        var target = camera;
        target.Zoom(scale, new Vec(ViewSize.X / 2, ViewSize.Y / 2));
        returnCamera = null;
        viewPlaced = false;
        Animate(target, 0.22);
    }

    /// You took over the camera mid-animation: it stops where it is, and windows moving on the
    /// canvas (Tidy Up) land.
    void CancelAnimation()
    {
        if (animation is not Animation a) return;
        animation = null;
        if (a.Moves != null)
            foreach (var (w, _, to) in a.Moves)
            {
                w.Frame = to;
                PictureMoved(w);
            }
        a.Completion?.Invoke();
    }

    /// The pointer may be over a different window with no move to tell us (the canvas moved).
    void HoverAtCursor()
    {
        GetCursorPos(out var p);
        hovered = WindowAt(new Vec(p.X - monitor.Left, p.Y - monitor.Top));
    }

    double UiScale => GetDpiForWindow(overlay.Hwnd) / 96.0;

    /// Enough of a window on screen to count as one you can see (the Mac's goesOnScreen): its top
    /// not above the screen, and at least 48 px of it in each direction.
    bool GoesOnScreen(RectD r)
    {
        var x0 = Math.Max(r.X, 0); var x1 = Math.Min(r.Right, ViewSize.X);
        var y0 = Math.Max(r.Y, 0); var y1 = Math.Min(r.Bottom, ViewSize.Y);
        return r.Y >= -1 && x1 - x0 >= 48 && y1 - y0 >= 48;
    }

    /// A wheel turn zoomed out, as on the Mac: the wheel (and a touchpad's two-finger scroll) pans;
    /// with Ctrl (and a touchpad pinch, which arrives as Ctrl+wheel) it zooms around the pointer;
    /// Shift turns a plain wheel sideways. `at`: the pointer, in screen coordinates (packed).
    void Wheel(bool horizontal, int delta, bool ctrl, bool shift, nint at)
    {
        var screen = new Vec((short)(at & 0xFFFF), (short)((at >> 16) & 0xFFFF));
        double step = 0.5 * UiScale; // 60 px a notch at 100%
        if (horizontal) PanBy(new Vec(-delta * step, 0));
        else if (ctrl) ZoomBy(Math.Pow(1.2, delta / 120.0), new Vec(screen.X - monitor.Left, screen.Y - monitor.Top));
        else if (shift) PanBy(new Vec(delta * step, 0));
        else PanBy(new Vec(0, delta * step));
    }

    // MARK: Dialogs over the canvas

    /// While the canvas is up it never covers a dialog asking you something (as on the Mac): a
    /// window on the canvas with a modal dialog up (the window itself disabled), or another app's
    /// always-on-top dialog. The dialog is shown over the canvas, so you can answer it where you
    /// are: made always-on-top for the while (the canvas is, and the newest goes in front), and put
    /// back when it closes or the canvas goes. Checked twice a second (nothing says one opened).
    void KeepDialogsVisible()
    {
        if (active) ShowDialogOverCanvas(AskingDialog());
    }

    void ShowDialogOverCanvas(nint dialog)
    {
        if (dialog == dialogOnTop) return;
        const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
        if (dialogOnTop != 0 && IsWindow(dialogOnTop) && !dialogWasTopmost)
            SetWindowPos(dialogOnTop, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        dialogOnTop = dialog;
        if (dialog == 0) return;
        dialogWasTopmost = (ExStyle(dialog) & WS_EX_TOPMOST) != 0;
        // Off the screen (by its window, wherever that is), it comes to the middle of the screen;
        // the hand-back puts it back by its window.
        var f = Dwm.FrameBounds(dialog);
        GetWindowRect(dialog, out var r);
        bool onScreen = f.Left >= monitor.Left && f.Top >= monitor.Top && f.Right <= monitor.Right && f.Bottom <= monitor.Bottom;
        int x = monitor.Left + (monitor.Width - f.Width) / 2 - (f.Left - r.Left), y = monitor.Top + (monitor.Height - f.Height) / 2 - (f.Top - r.Top);
        SetWindowPos(dialog, HWND_TOPMOST, x, y, 0, 0, onScreen ? flags : SWP_NOSIZE | SWP_NOACTIVATE);
        Log.Info($"dialog of {CanvasWindow.Describe(dialog)} shown over the canvas{(onScreen ? "" : ", brought onto the screen")}");
    }

    nint AskingDialog()
    {
        foreach (var w in windows)
            if (!IsWindowEnabled(w.Hwnd))
            {
                nint popup = GetLastActivePopup(w.Hwnd);
                if (popup != w.Hwnd && popup != 0 && IsWindowVisible(popup) && IsWindowEnabled(popup)) return popup;
            }
        nint found = 0;
        uint self = Kernel32.GetCurrentProcessId();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || (ExStyle(h) & WS_EX_TOPMOST) == 0 || ClassName(h) != "#32770") return true;
            GetWindowThreadProcessId(h, out uint pid);
            var f = Dwm.FrameBounds(h);
            if (pid == self || Dwm.IsCloaked(h) || f.Width < 100 || f.Height < 50) return true;
            found = h;
            return false;
        }, 0);
        return found;
    }

    // MARK: Closing

    const int CloseMinWidth = 220; // no × on pictures narrower than this (as on the Mac)
    const nint CloseTimer = 7, HotCornerTimer = 8, DialogTimer = 9;
    nint dialogOnTop; // the dialog shown over the canvas, or 0 (see KeepDialogsVisible)
    bool dialogWasTopmost;
    readonly HotCorner hotCorner = new();

    /// Whether `w` shows its × with the pointer over `pointer`: the window under the pointer
    /// shows one, and with it every window of the selection it's in. Not on small pictures, nor
    /// where a window in front covers the corner.
    bool CloseShown(CanvasWindow w, CanvasWindow? pointer, double k)
    {
        if (w != pointer && !(InGroup(pointer) && selected.Contains(w))) return false;
        var r = camera.ToView(w.Frame);
        if (r.W < CloseMinWidth * k) return false;
        var c = Marks.CloseRect(r, k);
        return !windows.Skip(windows.IndexOf(w) + 1).SelectMany(f => f.WithOwned()).Concat(w.Owned)
            .Any(f => camera.ToView(f.Frame).Intersects(c));
    }

    /// Closes `w`, or the whole selection if it's part of one: each is sent WM_CLOSE, as its own
    /// close button would. Their pictures go when the windows do (the sync after).
    void Close(CanvasWindow w)
    {
        List<CanvasWindow> ws = InGroup(w) ? windows.Where(selected.Contains).ToList() : [w];
        if (ws.Count > 1) selected.Clear();
        Log.Info(ws.Count == 1 ? $"close {w}" : $"close {ws.Count} windows");
        foreach (var x in ws) PostMessage(x.Hwnd, WM_CLOSE, 0, 0);
        closing.Clear();
        closing.AddRange(ws);
        SetTimer(overlay.Hwnd, CloseTimer, 500, 0);
        Redraw();
    }

    /// Ctrl+W: the selection if there is one, otherwise the window under the pointer.
    void CloseKey()
    {
        if ((windows.FirstOrDefault(selected.Contains) ?? hovered) is CanvasWindow w) Close(w);
    }

    /// Half a second after closing: a window still there with its input disabled has a dialog up
    /// ("Save changes?"), which the overlay would cover. Dive into it so it can be answered.
    void CloseSettled()
    {
        var asking = closing.FirstOrDefault(w => windows.Contains(w) && IsWindow(w.Hwnd) && IsWindowVisible(w.Hwnd) && !IsWindowEnabled(w.Hwnd));
        closing.Clear();
        if (asking == null || !active || gliding) return;
        Log.Info($"close: {asking} is asking something first: dive in");
        Exit(asking);
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
        Save(cleanExit: true);
        Originals.Restore(windows);
        paused = true;
        tray.SetTip("Expanse (paused)");
        Log.Info("paused");
    }

    /// Like a fresh launch: the canvas comes back from the layout saved by Pause.
    public void Resume()
    {
        if (!paused) return;
        paused = false;
        windows.Clear();
        departed.Clear();
        camera = Camera.Identity;
        initialSyncDone = false;
        lastSavedJson = null;
        Start();
        tray.SetTip("Expanse");
        Log.Info("resumed");
    }

    /// The layout snapshots, newest first: "12 minutes ago (8:30 AM)".
    IReadOnlyList<Tray.Item?> RestoreLayoutMenu()
    {
        var items = LayoutStore.Snapshots().Select(s => (Tray.Item?)new Tray.Item(LayoutStore.Describe(s), () =>
        {
            if (LayoutStore.Load(s) is SavedLayout layout) RestoreLayout(layout);
        })).ToList();
        if (items.Count == 0) items.Add(new Tray.Item("No earlier layouts yet", null, Enabled: false));
        return items;
    }

    IReadOnlyList<Tray.Item?> TrayMenu() => new Tray.Item?[]
    {
        new("Open Expanse\tCtrl+Alt+`", () => Toggle(), Enabled: !paused),
        new(paused ? "Resume Expanse" : "Pause Expanse", () => { if (paused) Resume(); else Pause(); }),
        null,
        new("Centre Current Window\tCtrl+Alt+C", CentreCurrentWindow, Enabled: !paused),
        new("Auto-Centre Focused Window\tCtrl+Alt+Shift+`", ToggleAutoCentre, settings.AutoCentre),
        new("Desktop Picture Background", () => { settings.Wallpaper = !settings.Wallpaper; settings.Save(); }, settings.Wallpaper),
        new("Show Window Names", () => { settings.ShowNames = !settings.ShowNames; settings.Save(); }, settings.ShowNames),
        new("Hot Corner", null, Children: HotCorner.Choices.Select(c => (Tray.Item?)new Tray.Item(c.title, () =>
        {
            settings.HotCorner = c.key;
            settings.Save();
            Log.Info($"hot corner: {c.key}");
        }, settings.HotCorner == c.key)).ToList()),
        new("Tidy Up Windows", Tidy, Enabled: !paused),
        new(Elevation.IsElevated ? "Include Admin Windows" : "Include Admin Windows…", ToggleAdminWindows, Elevation.IsElevated, Enabled: !paused),
        new("Restore Layout", null, Enabled: !paused, Children: RestoreLayoutMenu()),
        new("Restore All Windows", RestoreAll, Enabled: !paused),
        new("Welcome & Shortcuts…", ShowWelcome),
        null,
        new("Open at Login", () => LoginItem.Set(!LoginItem.IsOn), LoginItem.IsOn),
        new("Troubleshooting", null, Children: new Tray.Item?[]
        {
            new("Detailed Logging", ToggleDetailedLogging, settings.DetailedLogging),
            new("Copy Logs for a Bug Report", CopyLogs),
            new("Show Log File", ShowLogFile, Enabled: File.Exists(Log.FilePath)),
        }),
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
    /// `overview`: false rises at 100% without zooming out (moving around from where you are).
    public void Enter(bool zoomOut, bool sync = true, bool overview = true)
    {
        if (active)
        {
            if (zoomOut && !gliding) Animate(OverviewCamera(), 0.22);
            return;
        }
        ReadScreen();
        if (sync) Sync();
        previousForeground = GetForegroundWindow();
        active = true;
        gliding = !zoomOut;
        hovered = null;
        camera.Scale = 1;
        liveOrigin = camera.Origin;
        if (!pictures.Attach(Stack()) && pictures is SharedVisualPictures)
        {
            Log.Info("pictures: shared visuals failed for every window; falling back to DWM thumbnails");
            pictures.Dispose();
            pictures = new ThumbnailPictures(overlay.Hwnd);
            pictures.Attach(Stack());
        }
        Relayout();
        pictures.Flush();
        overlay.Show(monitor, activate: zoomOut);
        SetTimer(overlay.Hwnd, DialogTimer, 500, 0);
        Lift();
        if (zoomOut)
        {
            lastOverlayFocus = Focus(overlay.Hwnd);
            Log.Info($"zoom out: {windows.Count} windows; overlay foreground {lastOverlayFocus}");
            returnCamera = camera;
            if (!overview) { HoverAtCursor(); return; }
            // The pointer may already be over a window, with no move to tell us.
            Animate(OverviewCamera(), 0.22, () =>
            {
                HoverAtCursor();
                if (pendingTidy) { pendingTidy = false; Tidy(); }
                if (pendingRestore is SavedLayout restore) { pendingRestore = null; RestoreLayout(restore); }
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
        foreach (var w in Stack())
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
            FitToScreen(focus);
            if (viewPlaced && Math.Abs(camera.Scale - 1) < 0.001 && GoesOnScreen(camera.ToView(focus.Frame)))
            {
                // You put the view here: the window you picked is on screen, and the view stays.
                Log.Info($"view placed by you: diving into {focus} where it is");
                target = new Camera(new Vec(Math.Round(camera.Origin.X), Math.Round(camera.Origin.Y)), 1);
            }
            else
            {
                viewPlaced = false;
                target = FocusCamera(focus);
            }
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
        viewPlaced = false;
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
        var target = focus ?? CanvasWindowOf(previousForeground)
            ?? windows.LastOrDefault(w => camera.ToView(w.Frame).Intersects(View));
        if (target != null && IsWindow(target.Hwnd))
        {
            // Its dialog, if that's where you last were (or it's asking something, which leaves
            // the window itself disabled).
            nint popup = GetLastActivePopup(target.Hwnd);
            nint to = popup != 0 && IsWindowVisible(popup) && IsWindowEnabled(popup) ? popup : target.Hwnd;
            lastFocus = $"{target}: {Focus(to)}";
            lastOwnFocus = (target.Hwnd, Stopwatch.GetTimestamp());
            lastFocusedWindow = target.Hwnd;
            Log.Info($"focus {lastFocus}");
        }
        overlay.Hide();
        KillTimer(overlay.Hwnd, DialogTimer);
        ShowDialogOverCanvas(0);
        pictures.Detach();
        active = false;
        gliding = false;
        glideTarget = null;
        returnCamera = null;
        selected.Clear();
        spaceHeld = false;
        if (pendingRestoreAll) { pendingRestoreAll = false; RestoreAll(); }
        Log.Info($"handed back: moved {moved} windows, waited {waited} frames, {Ms(t0):0}ms");
        if (pendingSync) { pendingSync = false; SyncLive(); }
        ScheduleSave();
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

    /// Moves one window to its camera spot, and its dialogs with it. True if it had to move.
    bool Place(CanvasWindow w, bool saveOriginals = true)
    {
        bool moved = PlaceOne(w, saveOriginals);
        foreach (var o in w.Owned) PlaceOne(o, saveOriginals: false);
        return moved;
    }

    bool PlaceOne(CanvasWindow w, bool saveOriginals)
    {
        var v = camera.ToView(w.Frame);
        if (w.Maximised && !IsHungAppWindow(w.Hwnd))
        {
            // Where maximised windows sit, it's maximised; anywhere else it's an ordinary window
            // of the same size, moved like any other (see CanvasWindow.Maximised).
            bool home = Math.Abs(v.X - (work.Left - monitor.Left)) < 1 && Math.Abs(v.Y - (work.Top - monitor.Top)) < 1;
            if (home && w.RestoredByUs)
            {
                w.Maximise();
                Log.Info($"maximised {w} again");
            }
            else if (!home && IsZoomed(w.Hwnd))
            {
                if (w.Original == null) { GetWindowRect(w.Hwnd, out var before); w.Original = before; }
                w.RestoreForMoving(work);
                Originals.Save(windows); // it's to be maximised again if Expanse stops
                Log.Info($"restored {w} from maximised to move it");
            }
        }
        int x = (int)Math.Round(v.X) + monitor.Left - w.Insets.Left;
        int y = (int)Math.Round(v.Y) + monitor.Top - w.Insets.Top;
        x = Math.Clamp(x, -30000, 30000);
        y = Math.Clamp(y, -30000, 30000);
        if (otherMonitors.Count > 0)
        {
            int fx = x + w.Insets.Left, fy = y + w.Insets.Top;
            var frame = new RECT(fx, fy, fx + (int)w.Frame.W, fy + (int)w.Frame.H);
            if (!OnMainScreen(frame) && ParkingSpot(frame) is POINT p) (x, y) = (p.X - w.Insets.Left, p.Y - w.Insets.Top);
        }
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

    // MARK: Other monitors, scaling

    /// Whether a frame (screen coordinates) is at least partly on the main screen.
    bool OnMainScreen(RECT f) => Overlaps(f, monitor);

    /// On another monitor and not on the main screen at all.
    bool OnOtherMonitorOnly(RECT f) => !OnMainScreen(f) && otherMonitors.Any(m => Overlaps(f, m));

    static bool Overlaps(RECT a, RECT b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    static double Gap(RECT a, RECT b)
    {
        int dx = Math.Max(0, Math.Max(b.Left - a.Right, a.Left - b.Right));
        int dy = Math.Max(0, Math.Max(b.Top - a.Bottom, a.Top - b.Bottom));
        return Math.Sqrt((double)dx * dx + (double)dy * dy);
    }

    /// Where a window wholly off the main screen waits. At its camera spot, if that's clear of
    /// the other monitors and nearer the main screen than any of them; otherwise it'd show on one,
    /// and take that monitor's scaling (Windows rescales a window by the monitor it's nearest).
    /// Then just past the main screen's edge, the side it's heading first, on a side no other
    /// monitor reaches past at all (a window can grow as it's parked, rescaled back to the main
    /// screen's scaling, so being clear at its present size isn't enough); failing that, on a
    /// side where it's clear. Null: where it is will do (or nowhere is).
    POINT? ParkingSpot(RECT f)
    {
        bool Clear(RECT r) => otherMonitors.All(m => !Overlaps(r, m) && Gap(r, m) > Gap(r, monitor));
        if (Clear(f)) return null;
        int w = f.Width, h = f.Height;
        int cx = monitor.Left + (monitor.Width - w) / 2, cy = monitor.Top + (monitor.Height - h) / 2;
        const int Margin = 32; // past the window's shadow
        RECT At(int x, int y) => new(x, y, x + w, y + h);
        var sides = new (int beyond, bool open, RECT spot)[]
        {
            (monitor.Top - f.Bottom, otherMonitors.All(m => m.Top >= monitor.Top), At(cx, monitor.Top - Margin - h)),
            (f.Top - monitor.Bottom, otherMonitors.All(m => m.Bottom <= monitor.Bottom), At(cx, monitor.Bottom + Margin)),
            (monitor.Left - f.Right, otherMonitors.All(m => m.Left >= monitor.Left), At(monitor.Left - Margin - w, cy)),
            (f.Left - monitor.Right, otherMonitors.All(m => m.Right <= monitor.Right), At(monitor.Right + Margin, cy)),
        };
        var order = sides.OrderByDescending(s => s.beyond).ToList();
        foreach (var (_, open, spot) in order)
            if (open) return new POINT(spot.Left, spot.Top);
        foreach (var (_, _, spot) in order)
            if (Clear(spot)) return new POINT(spot.Left, spot.Top);
        return null;
    }

    /// The main screen's scaling changed (100% to 125%, say). Windows grows most windows by the
    /// same ratio and some apps don't follow, so the canvas follows Windows: every spot and home
    /// spot spreads from the middle of the view, and every chosen size grows, by `r`. The screen
    /// then holds less of the canvas, and fitting the layout to it (the display-change pass)
    /// brings each window to its chosen size, as for a smaller screen. As on the Mac, where scaling
    /// changes the screen's size in points and windows keep theirs.
    void ScaleCanvas(double r)
    {
        var c = camera.Center(ViewSize);
        Vec P(Vec p) => new(Layout.Round(c.X + (p.X - c.X) * r), Layout.Round(c.Y + (p.Y - c.Y) * r));
        SizeD Z(SizeD s) => new(Layout.Round(s.W * r), Layout.Round(s.H * r));
        foreach (var w in windows.Concat(departed.Values))
        {
            var owned = w.Owned.Select(o => (o, o.Frame.Origin)).ToList();
            // Maximised where it sits, Windows fits it to the screen as it is.
            if (!(w.Maximised && !w.RestoredByUs))
            {
                var o = P(w.Frame.Origin);
                w.Frame = w.Frame with { X = o.X, Y = o.Y }; // sizes are read from the windows
                if (w.Home is Vec home) w.Home = P(home);
            }
            if (w.Preferred.W > 0) w.Preferred = Z(w.Preferred);
            if (w.LastSized is SizeD l) w.LastSized = Z(l);
            foreach (var (o, at) in owned)
            {
                var p = P(at);
                o.Frame = o.Frame with { X = p.X, Y = p.Y };
            }
            w.Placed = null;
        }
        foreach (var e in savedPool) LayoutStore.Scale(e, c, r);
        Log.Info($"scaling {canvasDpi * 100 / 96}% -> {dpi * 100 / 96}%: canvas scaled by {r:0.###}");
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
        string from = CanvasWindow.Describe(before);
        bool returned = SetForegroundWindow(hwnd);
        // Accepted, it can still take a while to show (a window just maximised, say): an Alt tap
        // then would land in it, and WinUI apps show their key tips.
        if (IsForeground(hwnd, returned ? 25 : 5)) return $"direct (from {from})";
        var steps = $"direct refused (returned {returned}, from {from})";
        // A key no app acts on, first: an Alt tap lands in the window in front, and WinUI apps
        // (Notepad, Settings) show their key tips for it.
        TapKey(0xFF);
        returned = SetForegroundWindow(hwnd);
        if (IsForeground(hwnd)) return steps + ", ok after a null key";
        steps += $", null key refused (returned {returned})";
        TapKey(VK_MENU);
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
    static bool IsForeground(nint hwnd, int tries = 5)
    {
        for (int i = 0; i < tries; i++)
        {
            if (GetForegroundWindow() == hwnd) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    static void TapKey(int vk)
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = (ushort)vk;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = (ushort)vk;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    // MARK: Camera targets

    /// `w` at 100%, centred in the work area (below the top if it's taller).
    Camera FocusCamera(CanvasWindow w)
    {
        double ax = work.Left - monitor.Left, ay = work.Top - monitor.Top;
        // Maximised: exactly where maximised windows sit (Place maximises it there).
        if (w.Maximised) return new Camera(new Vec(Math.Round(w.Frame.X - ax), Math.Round(w.Frame.Y - ay)), 1);
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
        // GDI labels and the ring go while moving; the visual-tree ones move along (but not on a
        // glide or dive: gliding hides them).
        if (pictures.DrawsDecorations) Redraw(); else overlay.Repaint();
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
                PictureMoved(w);
            }
        Relayout();
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
            if (active && !gliding) Redraw();
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
            case WM_TIMER when wParam == RelayoutTimer:
                KillTimer(overlay.Hwnd, RelayoutTimer);
                RelayoutForScreen(final: false);
                return 0;
            case WM_TIMER when wParam == FinalRelayoutTimer:
                KillTimer(overlay.Hwnd, FinalRelayoutTimer);
                RelayoutForScreen(final: true);
                return 0;
            case WM_TIMER when wParam == SettleTimer:
                KillTimer(overlay.Hwnd, SettleTimer);
                suspendTracking = false;
                Log.Info("display change settled");
                return 0;
            case WM_SETTINGCHANGE when (int)wParam == 0x2F: // SPI_SETWORKAREA: the taskbar changed
                DisplaysChanging();
                return null;
            case WM_TIMER when wParam == SaveTimer:
                KillTimer(overlay.Hwnd, SaveTimer);
                Save();
                return 0;
            case WM_TIMER when wParam == HotCornerTimer:
                desktopInput.Armed = !active && !paused;
                if (!paused && !gliding && animation == null && drag == null && hotCorner.Poll(settings.HotCorner, monitor))
                {
                    Log.Info($"hot corner ({settings.HotCorner})");
                    Toggle();
                }
                return 0;
            case WM_TIMER when wParam == DialogTimer:
                KeepDialogsVisible();
                return 0;
            case WM_TIMER when wParam == CloseTimer:
                KillTimer(overlay.Hwnd, CloseTimer);
                CloseSettled();
                return 0;
            case WM_TIMER when wParam == FocusTimer:
                KillTimer(overlay.Hwnd, FocusTimer);
                FocusSettled();
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
                minimap.ThemeChanged();
                return null;
            case WM_HOTKEY:
                switch ((int)wParam)
                {
                    case HotkeyCanvas: Log.Info("hotkey"); Toggle(); break;
                    case HotkeyLeft: Navigate(Direction.Left); break;
                    case HotkeyRight: Navigate(Direction.Right); break;
                    case HotkeyUp: Navigate(Direction.Up); break;
                    case HotkeyDown: Navigate(Direction.Down); break;
                    case HotkeyCentre: CentreCurrentWindow(); break;
                    case HotkeyAutoCentre: ToggleAutoCentre(); break;
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
                if (active && !gliding && animation == null) Press(p, shift: ((int)wParam & MK_SHIFT) != 0);
                return 0;
            }
            case WM_LBUTTONUP:
                EndDrag(click: true);
                return 0;
            case WM_CAPTURECHANGED:
                EndDrag(click: false);
                return 0;
            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
            {
                // As on the Mac: the wheel (and a touchpad's two-finger scroll) pans; with Ctrl (and
                // a touchpad pinch, which arrives as Ctrl+wheel) it zooms around the pointer. Shift
                // turns a plain wheel sideways.
                if (!active || gliding || drag != null) return 0;
                int keys = (int)(wParam & 0xFFFF);
                Wheel(msg == WM_MOUSEHWHEEL, (short)((wParam >> 16) & 0xFFFF), (keys & MK_CONTROL) != 0, (keys & MK_SHIFT) != 0, lParam);
                return 0;
            }
            case DesktopInput.WM_DESKTOP_CLICK:
                if (!active && !paused && !gliding &&
                    DesktopInput.NothingSelected(new POINT((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF))))
                {
                    Log.Info("click on the desktop");
                    Enter(zoomOut: true);
                }
                return 0;
            case DesktopInput.WM_DESKTOP_WHEEL:
            {
                // From 100%: the overlay rises where you are, and this turn (and the ones after,
                // which reach the overlay itself) moves around the canvas, as on the Mac.
                if (active || paused || gliding) return 0;
                Log.Info("scroll on the desktop: moving around from 100%");
                Enter(zoomOut: true, overview: false);
                int flags = (int)(wParam & 0xFFFF);
                Wheel((flags & DesktopInput.Horizontal) != 0, (short)((wParam >> 16) & 0xFFFF),
                    (flags & DesktopInput.Ctrl) != 0, (flags & DesktopInput.Shift) != 0, lParam);
                return 0;
            }
            case WM_SETCURSOR when active && (spaceHeld || drag?.What == DragKind.Pan):
                SetCursor(LoadCursor(0, IDC_SIZEALL));
                return 1;
            case WM_KEYUP when (int)wParam == VK_SPACE:
                spaceHeld = false;
                if (drag?.What != DragKind.Pan) SetCursor(LoadCursor(0, IDC_ARROW));
                return 0;
            case WM_KEYDOWN:
            {
                if (!active || gliding) return 0;
                bool ctrl = GetKeyState(VK_CONTROL) < 0;
                switch ((int)wParam)
                {
                    case VK_ESCAPE when selected.Count > 0: selected.Clear(); Redraw(); break;
                    case VK_ESCAPE: Exit(null); break;
                    case VK_RETURN when windows.Count > 0: Exit(hovered ?? windows[^1]); break;
                    case VK_SPACE when !spaceHeld:
                        spaceHeld = true;
                        SetCursor(LoadCursor(0, IDC_SIZEALL));
                        break;
                    case VK_A when ctrl: selected.UnionWith(windows); Redraw(); break;
                    case VK_W when ctrl: CloseKey(); break;
                    case VK_T when !ctrl: Tidy(); break;
                    case VK_0 or VK_NUMPAD0 when !ctrl:
                        CancelAnimation();
                        returnCamera = null;
                        Animate(FitCamera(1), 0.22);
                        break;
                    case VK_1 or VK_NUMPAD1 when !ctrl: ZoomKey(1); break;
                    case VK_OEM_MINUS or VK_SUBTRACT: ZoomKey(camera.Scale / 1.5); break;
                    case VK_OEM_PLUS or VK_ADD: ZoomKey(camera.Scale * 1.5); break;
                    case VK_LEFT: Navigate(Direction.Left); break;
                    case VK_RIGHT: Navigate(Direction.Right); break;
                    case VK_UP: Navigate(Direction.Up); break;
                    case VK_DOWN: Navigate(Direction.Down); break;
                }
                return 0;
            }
            case WM_SYSCOMMAND when (wParam & 0xFFF0) == SC_KEYMENU:
                return 0; // Alt (ours, from the focus fallback, or the user's) mustn't enter menu mode

            case WM_DISPLAYCHANGE:
                Log.Info("display change");
                DisplaysChanging();
                return null;
            case WM_DPICHANGED: // the main screen's scaling (the overlay lives on it)
                Log.Info($"scaling change: {(int)(wParam & 0xFFFF) * 100 / 96}%");
                DisplaysChanging();
                return 0;
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
        var w = minimap.Panel.Contains(p) ? null : WindowAt(p);
        if (w == hovered) return;
        hovered = w;
        Redraw();
    }

    /// Positions the pictures for the camera, with the titles and hover ring, and puts it all on
    /// screen at once.
    void Relayout()
    {
        pictures.Layout(Stack(), camera, View);
        Redraw();
    }

    /// Titles and the hover ring: in the pictures' visual tree when they can draw there, otherwise
    /// on the overlay's backdrop (only while still: GDI can't keep up with the camera).
    void Redraw()
    {
        if (!pictures.DrawsDecorations)
        {
            if (animation == null) overlay.Invalidate();
            pictures.Present();
            return;
        }
        bool show = active && !gliding;
        double k = UiScale;
        pictures.SetLabels(show && settings.ShowNames ? Labels().Select(l => new Label(l.w.Hwnd, LabelText(l.w), l.view)).ToList() : [], k);
        pictures.SetMarks(show ? CurrentMarks(k) : Marks.None, k);
        pictures.SetMinimap(show ? MinimapNow(k) : null, k);
        pictures.Present();
    }

    MinimapDrawing MinimapNow(double k) =>
        minimap.Update(windows.Select(w => w.Frame).ToList(), camera.ToCanvas(View), camera.Scale, ViewSize, k);

    /// Rings round the hovered and selected windows, the box round a selection of more than one,
    /// the marquee, and the × buttons showing.
    Marks CurrentMarks(double k)
    {
        var rings = windows.Where(w => w == hovered || selected.Contains(w))
            .Select(w => (w.Hwnd, camera.ToView(w.Frame), selected.Contains(w))).ToList();
        var picked = windows.Where(selected.Contains).Select(w => camera.ToView(w.Frame)).ToList();
        RectD? box = picked.Count > 1 ? picked.Skip(1).Aggregate(picked[0], (a, b) => a.Union(b)) : null;
        var closes = drag == null ? windows.Where(w => CloseShown(w, hovered, k)).Select(w => (w.Hwnd, camera.ToView(w.Frame))).ToList() : [];
        return new Marks(rings, box, marquee, closes);
    }

    /// Windows that get a title, bottom-most first: not tiny, in view, and not with their top
    /// under a window in front (the title would sit on that one's).
    IEnumerable<(CanvasWindow w, RectD view)> Labels()
    {
        for (int i = 0; i < windows.Count; i++)
        {
            var r = camera.ToView(windows[i].Frame);
            if (r.W < 80 || !r.Intersects(View)) continue;
            var top = new Vec(r.X + r.W / 2, r.Y + 1);
            if (windows.Skip(i + 1).Any(f => camera.ToView(f.Frame).Contains(top))) continue;
            yield return (windows[i], r);
        }
    }

    /// As on the Mac: the app's name; hovered, "App — window title".
    string LabelText(CanvasWindow w) =>
        w == hovered && w.Title.Length > 0 && w.Title != w.AppName ? $"{w.AppName} — {w.Title}" : w.AppName;

    // MARK: Drawing

    /// The backdrop, plus (zoomed out and still) a label above each window and a ring around
    /// the one under the pointer. Both sit outside the pictures, so DWM doesn't cover them.
    void Paint(nint hdc, int width, int height)
    {
        wallpaper.Draw(hdc, width, height, settings.Wallpaper);
        if (!active || gliding || animation != null) return;
        // DWM thumbnails can't have anything drawn over them: titles, ring and guides go here,
        // under them. Shared visuals draw all that in their own tree (Redraw).
        if (pictures.DrawsDecorations) return;
        double k = GetDpiForWindow(overlay.Hwnd) / 96.0;
        nint font = Gdi32.CreateFont(-(int)(13 * k), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        nint pill = Gdi32.CreateSolidBrush(Gdi32.RGB(32, 32, 34));
        nint oldFont = Gdi32.SelectObject(hdc, font);
        nint oldPen = Gdi32.SelectObject(hdc, Gdi32.GetStockObject(Gdi32.NULL_PEN));
        nint oldBrush = Gdi32.SelectObject(hdc, pill);
        Gdi32.SetBkMode(hdc, Gdi32.TRANSPARENT);
        Gdi32.SetTextColor(hdc, Gdi32.RGB(240, 240, 240));
        foreach (var (w, r) in settings.ShowNames ? Labels() : [])
        {
            string label = LabelText(w);
            var measure = new RECT(0, 0, 4000, 100);
            DrawText(hdc, label, -1, ref measure, DT_SINGLELINE | DT_NOPREFIX | DT_CALCRECT);
            int pad = (int)(10 * k), h = (int)(22 * k);
            int lw = (int)Math.Min(measure.Width + pad * 2, r.W);
            int lx = (int)(r.X + (r.W - lw) / 2), ly = (int)(r.Y - h - 6 * k);
            Gdi32.RoundRect(hdc, lx, ly, lx + lw, ly + h, h, h);
            var text = new RECT(lx + pad, ly, lx + lw - pad, ly + h);
            DrawText(hdc, label, -1, ref text, DT_SINGLELINE | DT_VCENTER | DT_CENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
        }
        // Rings, the selection box and the marquee's outline (no × buttons: they'd sit on the
        // pictures, which cover the backdrop; Ctrl+W still closes).
        var marks = CurrentMarks(k);
        nint blue = Gdi32.CreateSolidBrush(Gdi32.RGB(76, 154, 255));
        void Outline(RectD o, int t)
        {
            int x0 = (int)Math.Round(o.X), y0 = (int)Math.Round(o.Y), x1 = (int)Math.Round(o.Right), y1 = (int)Math.Round(o.Bottom);
            foreach (var s in new[] { new RECT(x0, y0, x1, y0 + t), new RECT(x0, y1 - t, x1, y1), new RECT(x0, y0, x0 + t, y1), new RECT(x1 - t, y0, x1, y1) })
            {
                var strip = s;
                FillRect(hdc, ref strip, blue);
            }
        }
        int gap = Math.Max(1, (int)Math.Round(2 * k)), thin = Math.Max(1, (int)Math.Round(k));
        foreach (var (_, r, isSelected) in marks.Rings)
        {
            int t = Math.Max(2, (int)Math.Round((isSelected ? 4 : 3) * k));
            Outline(r.Inset(-(gap + t), -(gap + t)), t);
        }
        if (marks.SelectionBox is RectD box) Outline(box.Inset(-8 * k, -8 * k), thin);
        if (marks.Marquee is RectD m) Outline(m, thin);
        // The minimap, plainly (DWM thumbnails cover anything drawn here, so it's drawn where it
        // mostly won't be under one: the bottom-right corner).
        var map = MinimapNow(k);
        nint panelBrush = Gdi32.CreateSolidBrush(map.Dark ? Gdi32.RGB(31, 31, 33) : Gdi32.RGB(247, 247, 250));
        nint blockBrush = Gdi32.CreateSolidBrush(map.Dark ? Gdi32.RGB(118, 118, 120) : Gdi32.RGB(198, 198, 200));
        RECT R(RectD r) => new((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));
        var panelRect = R(map.Panel);
        FillRect(hdc, ref panelRect, panelBrush);
        foreach (var b in map.Windows) { var br = R(b); FillRect(hdc, ref br, blockBrush); }
        Outline(map.Viewport.Inset(-Minimap.RingOutset * k, -Minimap.RingOutset * k), Math.Max(1, (int)Math.Round((map.FramesAll ? 1 : 2) * k)));
        Gdi32.DeleteObject(panelBrush);
        Gdi32.DeleteObject(blockBrush);
        Gdi32.DeleteObject(blue);
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
        // Not the arguments of pick and close: they're window titles.
        Log.Info($"command: {(verb is "pick" or "close" && arg.Length > 0 ? verb + " (title)" : request.Text.Trim())}");
        switch (verb)
        {
            case "state": request.Reply(State()); break;
            case "windows": request.Reply(CanvasWindow.Describe()); break;
            case "centre": CentreCurrentWindow(); WhenIdle(request); break;
            case "restoreall": RestoreAll(); request.Reply("done"); break;
            case "fit": // the 0 key, from anywhere: zoomed out, everything in view
                if (!active) Enter(zoomOut: true);
                else { CancelAnimation(); returnCamera = null; Animate(FitCamera(1), 0.22); }
                WhenIdle(request);
                break;
            case "zoom" when double.TryParse(arg, System.Globalization.CultureInfo.InvariantCulture, out double scale):
                if (!active) Enter(zoomOut: true, overview: false);
                ZoomKey(Math.Clamp(scale, Camera.MinScale, Camera.MaxScale));
                WhenIdle(request);
                break;
            case "pan":
            {
                var d = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out double n) ? n : 0).ToArray();
                if (!active) Enter(zoomOut: true, overview: false);
                if (d.Length == 2) PanBy(new Vec(-d[0], -d[1]));
                request.Reply(State());
                break;
            }
            case "focus": // a window of the app named (its name or exe), as expanse://focus?app=
                if (windows.LastOrDefault(w => w.AppName.Equals(arg, StringComparison.OrdinalIgnoreCase) ||
                                               w.App.Equals(arg, StringComparison.OrdinalIgnoreCase)) is CanvasWindow f)
                {
                    if (active) Exit(f); else Glide(f);
                    WhenIdle(request);
                }
                else request.Reply($"no window of {arg}");
                break;
            case "login": // Open at Login on or off
                if (arg is "on" or "off") LoginItem.Set(arg == "on");
                request.Reply($"open at login {(LoginItem.IsOn ? "on" : "off")}");
                break;
            case "corner": // Hot Corner: off, all, topLeft, topRight, bottomLeft, bottomRight
                if (HotCorner.Choices.Any(c => c.key == arg)) { settings.HotCorner = arg; settings.Save(); }
                request.Reply($"hot corner {settings.HotCorner}");
                break;
            case "welcome": ShowWelcome(); request.Reply("shown"); break;
            case "names": // Show Window Names on or off
                settings.ShowNames = arg == "on";
                settings.Save();
                request.Reply($"window names {(settings.ShowNames ? "shown" : "hidden")}");
                break;
            case "logging": // Detailed Logging on or off
                if ((arg == "on") != settings.DetailedLogging) ToggleDetailedLogging();
                request.Reply($"detailed logging {(settings.DetailedLogging ? "on" : "off")}");
                break;
            case "copylogs": CopyLogs(); request.Reply("copied"); break;
            case "admin": // as Include Admin Windows on the tray menu
                request.Reply(Elevation.IsElevated ? "starting again as you" : "starting again as administrator (Windows asks first)");
                ToggleAdminWindows();
                break;
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
                Relayout();
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
            case "screen":
            {
                // Pretend the screen is W x H (the Mac's screen?w=&h=), or the real one again.
                var v = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(double.Parse).ToArray();
                screenOverride = v.Length == 2 ? new SizeD(v[0], v[1]) : null;
                DisplaysChanging(100);
                request.Reply(screenOverride is SizeD o ? $"pretending the screen is {o.W:0}x{o.H:0}" : "the real screen");
                break;
            }
            case "monitor":
            {
                // Pretend there's another monitor at X Y W H (testing several monitors), or none.
                var v = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                pretendMonitors.Clear();
                if (v.Length == 4) pretendMonitors.Add(new RECT(v[0], v[1], v[0] + v[2], v[1] + v[3]));
                DisplaysChanging(100);
                request.Reply(v.Length == 4 ? $"pretending there's a monitor at {pretendMonitors[0]}" : "no pretend monitors");
                break;
            }
            case "snapshot":
                Save();
                if (lastLayout != null) Snapshot(lastLayout);
                request.Reply(string.Join("\n", LayoutStore.Snapshots().Select(LayoutStore.Describe)));
                break;
            case "restore" when int.TryParse(arg, out int which):
            {
                var snapshots = LayoutStore.Snapshots();
                if (which < 0 || which >= snapshots.Count || LayoutStore.Load(snapshots[which]) is not SavedLayout layout)
                {
                    request.Reply($"no snapshot {which} (there are {snapshots.Count})");
                    break;
                }
                RestoreLayout(layout);
                WhenIdle(request);
                break;
            }
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
                request.Reply("commands: state, log [N], toggle, zoomout, back, pick <title|app|0xHWND>, close <same>, view X Y SCALE, pictures shared|dwm, hop left|right|up|down, tidy, screen [W H], snapshot, restore N, minimize|unminimize <same>, pause, resume, autocentre on|off, bench [SECONDS], quit");
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
            otherMonitors = otherMonitors.Select(m => m.ToString()),
            dpi,
            elsewhere = elsewhere.Select(h => $"0x{h:X} {Title(h)}"),
            workArea = work.ToString(),
            foreground = new { hwnd = $"0x{fg:X}", title = Title(fg) },
            hovered = hovered?.ToString(),
            selected = windows.Where(selected.Contains).Select(w => w.ToString()),
            marquee = marquee?.ToString(),
            spaceHeld,
            returnCamera = returnCamera?.ToString(),
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
                owned = w.Owned.Select(o => $"{o} canvas {o.Frame} screen {Dwm.FrameBounds(o.Hwnd).ToRectD()}"),
                maximised = w.Maximised ? (w.RestoredByUs ? "restored by Expanse" : "yes") : null,
                dpi = GetDpiForWindow(w.Hwnd),
                original = w.Original?.ToString(),
                home = w.Home?.ToString(),
                preferred = $"{w.Preferred.W:0}x{w.Preferred.H:0}",
            }),
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// Puts every window back where it was before Expanse moved it, then quits.
    // MARK: Centring, Restore All

    /// Ctrl+Alt+C: the window you're in, to the middle of the screen at 100% (the Mac's ⌃⌥C).
    public void CentreCurrentWindow()
    {
        if (paused || gliding) return;
        var w = active ? (hovered ?? windows.LastOrDefault()) : CanvasWindowOf(GetForegroundWindow()) ?? CanvasWindowOf(lastFocusedWindow);
        if (w == null) return;
        Log.Info($"centre {w}");
        if (active) Exit(w); else Glide(w);
    }

    /// Ctrl+Alt+Shift+`, or the tray: auto-centring on or off (off suits side-by-side work).
    void ToggleAutoCentre()
    {
        settings.AutoCentre = !settings.AutoCentre;
        settings.Save();
        Log.Info($"auto-centre {(settings.AutoCentre ? "on" : "off")}");
        tray.Notify(settings.AutoCentre ? "Auto-centre on" : "Auto-centre off",
            settings.AutoCentre ? "A window you switch to glides into view." : "Switching windows leaves the view where it is.");
    }

    bool pendingRestoreAll;

    /// The last resort (tray): every window off the screen comes into view, stacked down and to
    /// the right from the top middle, and stays there on the canvas. As on the Mac.
    public void RestoreAll()
    {
        if (paused || gliding) return;
        if (active) { pendingRestoreAll = true; Exit(null); return; }
        var view = new RectD(0, 0, ViewSize.X, ViewSize.Y);
        int i = 0;
        foreach (var w in windows)
        {
            var r = camera.ToView(w.Frame);
            if (view.Inset(-1, -1).Contains(r)) continue;
            var at = new Vec(view.W / 2 - r.W / 2 + i % 8 * 28, work.Top - monitor.Top + 40 + i % 8 * 28);
            var before = w.Frame.Origin;
            var c = camera.ToCanvas(at);
            w.Frame = w.Frame with { X = Layout.Round(c.X), Y = Layout.Round(c.Y) };
            NoteUserMove(w, before);
            Place(w);
            i++;
        }
        Log.Info($"restore all: {i} windows brought into view");
        ScheduleSave();
    }

    // MARK: Welcome

    /// Shortcuts another app holds (Program fills this in), for Welcome & Shortcuts to mention.
    public readonly List<string> ShortcutProblems = new();

    /// Welcome & Shortcuts: an ordinary window, so zoomed out it waits until the canvas has gone,
    /// or it would open underneath (as on the Mac).
    public void ShowWelcome()
    {
        if (active && !gliding) Exit(null);
        // In front: Windows lets a program take the foreground only now and then (see Focus).
        if (Welcome.Show(settings.HotCorner, ShortcutProblems) is var w and not 0) Log.Info($"welcome: focus {Focus(w)}");
        if (!settings.WelcomeShown) { settings.WelcomeShown = true; settings.Save(); }
    }

    /// First launch: Welcome & Shortcuts opens by itself.
    public void WelcomeIfNew()
    {
        if (!settings.WelcomeShown) ShowWelcome();
    }

    // MARK: Troubleshooting

    void ToggleDetailedLogging()
    {
        settings.DetailedLogging = !settings.DetailedLogging;
        settings.Save();
        if (settings.DetailedLogging) Log.StartFresh();
        Log.ToFile = settings.DetailedLogging;
        Log.Info($"detailed logging {(settings.DetailedLogging ? "on" : "off")}");
    }

    void CopyLogs()
    {
        var lines = new[]
        {
            $"Canvas: {windows.Count} windows, {(paused ? "paused" : "running")}, auto-centre {(settings.AutoCentre ? "on" : "off")}, hot corner {settings.HotCorner}",
            $"Desktop picture background {(settings.Wallpaper ? "on" : "off")}, window names {(settings.ShowNames ? "shown" : "hidden")}, pictures {pictures.Name}, detailed logging {(settings.DetailedLogging ? "on" : "off")}",
        };
        bool copied = BugReport.Copy(overlay.Hwnd, lines);
        Log.Info(copied ? "copied logs for a bug report" : "copy logs: the clipboard was busy");
        tray.Notify(copied ? "Logs copied" : "Couldn't copy the logs",
            copied ? (settings.DetailedLogging ? "Paste them into an issue, with a line about what happened."
                                               : "Paste them into an issue. For more, turn on Detailed Logging and try again.")
                   : "The clipboard was busy. Try again in a moment.");
    }

    static void ShowLogFile()
    {
        if (File.Exists(Log.FilePath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Log.FilePath}\"") { UseShellExecute = false });
    }

    // MARK: Admin windows

    /// Admin windows seen, each logged once; the first ever also gets a note from the tray icon.
    readonly HashSet<nint> adminWindows = new();

    void NoteAdminWindows(List<nint> found)
    {
        // Off the canvas, an admin window is wherever you put it: a spot saved from when Expanse
        // ran as administrator is stale, and taking it back later would move it out from under you.
        foreach (var h in found)
        {
            GetWindowThreadProcessId(h, out uint pid);
            savedPool.RemoveAll(e => e.Hwnd == h && e.Pid == pid);
        }
        foreach (var h in found.Where(adminWindows.Add))
        {
            Log.Info($"admin window {CanvasWindow.Describe(h)} stays off the canvas: Expanse isn't running as administrator");
            if (settings.AdminNoteShown) continue;
            string title = Title(h);
            if (title.Length > 40) title = title[..39] + "…";
            tray.Notify("Expanse can't move admin windows",
                $"\"{title}\" runs as administrator. To include such windows, choose Include Admin Windows from Expanse's menu.");
            settings.AdminNoteShown = true;
            settings.Save();
        }
        adminWindows.RemoveWhere(h => !IsWindow(h));
    }

    /// Include Admin Windows: Expanse starts again as administrator (Windows asks you), or, ticked
    /// already, as you. The new one takes over where this one is (HandOver).
    void ToggleAdminWindows()
    {
        bool want = !Elevation.IsElevated;
        if (active) return;
        if ((want ? Elevation.StartElevated() : Elevation.StartNormal()) is string why)
        {
            Log.Info($"{(want ? "run as administrator" : "run as you")}: {why}");
            return;
        }
        settings.RunAsAdmin = want;
        settings.Save();
        Log.Info(want ? "handing over to Expanse run as administrator" : "handing over to Expanse run as you");
        // Run as you, the next one can't move admin windows: they go back where they were first,
        // or one parked off screen would be stranded there.
        if (!want && windows.Where(Elevation.AdminWindow).ToList() is { Count: > 0 } admin)
        {
            Originals.Restore(admin);
            Log.Info($"put {admin.Count} admin windows back first: Expanse run as you can't move them");
        }
        HandOver();
    }

    /// Quits leaving everything as it is for the Expanse taking over: windows where they are, the
    /// layout saved, and where they were before Expanse moved them (originals.json) kept for it.
    void HandOver()
    {
        events.Dispose();
        desktopInput.Dispose();
        tray.Dispose();
        Save(handOver: true);
        Originals.Save(windows);
        PostQuitMessage(0);
    }

    public void Quit()
    {
        events.Dispose();
        desktopInput.Dispose();
        tray.Dispose();
        if (!paused)
        {
            Save(cleanExit: true);
            Originals.Restore(windows);
        }
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
