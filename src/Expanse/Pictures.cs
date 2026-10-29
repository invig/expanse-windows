using Expanse.Native;

namespace Expanse;

/// Live window pictures on the overlay, laid out by the camera. Two ways to get them from DWM:
/// shared thumbnail visuals (the default) and plain DWM thumbnails (the fallback).
interface IPictures : IDisposable
{
    string Name { get; }
    int Count { get; }

    /// True once the pictures can't be drawn any more (the compositor restarted, or the
    /// graphics driver was reset), so the caller makes new ones.
    bool Lost { get; }

    /// A picture for each window, bottom-most first. False if none could be made at all (so
    /// the caller can fall back to the other kind).
    bool Attach(IEnumerable<CanvasWindow> bottomToTop);

    /// A picture for a window that appeared while the canvas is up, on top of the others.
    bool Add(CanvasWindow w);

    /// Drops the picture of a window that's gone.
    void Remove(CanvasWindow w);

    /// Puts `w`'s picture on top of the others.
    void Raise(CanvasWindow w);

    void Detach();

    /// The real desktop under every picture (its picture as Windows draws it, and its icons), live,
    /// over `screen`; none with null, or where the pictures can't go over it (see SharedVisualPictures).
    void SetDesktop(RECT? screen);

    /// Positions every picture for `camera` (view = the overlay's client area).
    void Layout(IReadOnlyList<CanvasWindow> windows, Camera camera, RectD view);

    /// `w`'s canvas frame changed (dragged, tidied): its picture follows.
    void Moved(CanvasWindow w, Camera camera, RectD view);

    /// The real window changed size (fitted to the screen): its picture takes the new size, in
    /// the same place in the stack.
    void Resized(CanvasWindow w);

    /// Snapping guides, in view coordinates, over the pictures. False if this kind can't draw
    /// over its pictures (the caller draws them underneath instead).
    bool SetGuides(IReadOnlyList<(Vec a, Vec b)> lines, double thickness);

    /// Whether titles and marks are drawn over the pictures (SetLabels, SetMarks);
    /// otherwise the overlay draws them on its backdrop, under the pictures.
    bool DrawsDecorations { get; }

    /// The titles to show, bottom-most window first, each centred above its picture (view
    /// coordinates). `scale` is the DPI scale.
    void SetLabels(IReadOnlyList<Label> labels, double scale);

    /// Our marks over the pictures: rings (hovered, selected), the box around a selection, the
    /// marquee being dragged, and close buttons.
    void SetMarks(Marks marks, double scale);

    /// The minimap (bottom right, zoomed out), or none. False if this kind can't draw it (the
    /// caller draws it on the backdrop instead).
    bool SetMinimap(MinimapDrawing? minimap, double scale);

    /// The Desktops panel beside the minimap, or none. False if this kind can't draw it.
    bool SetDesktops(DesktopsDrawing? desktops, double scale);

    /// Puts everything changed since the last call on screen, in one go (one composition
    /// commit): pictures, titles, ring and guides move together.
    void Present();

    /// Waits until the last layout is on screen: before the overlay is shown, so it never
    /// appears without its pictures.
    void Flush();
}

readonly record struct Label(nint Hwnd, string Text, RectD Picture);

/// View coordinates. Rings and close buttons belong to a window (its handle), and stack with its
/// picture. `Closes`: the pictures showing a close button (see Marks.CloseRect).
readonly record struct Marks(IReadOnlyList<(nint hwnd, RectD picture, bool selected)> Rings, RectD? SelectionBox, RectD? Marquee,
    IReadOnlyList<(nint hwnd, RectD picture)> Closes)
{
    public static readonly Marks None = new([], null, null, []);

    /// The close button of a picture drawn at `r`: 26 px (at 100%), 10 px in from the top-right
    /// corner, as on the Mac.
    public static RectD CloseRect(RectD r, double scale) =>
        new(r.Right - 36 * scale, r.Y + 10 * scale, 26 * scale, 26 * scale);
}

static class Pictures
{
    /// Shared visuals unless `kind` is "dwm", or DirectComposition isn't available.
    public static IPictures Create(nint overlay, string kind)
    {
        if (kind != "dwm")
        {
            try { return new SharedVisualPictures(overlay, failForTesting: kind == "shared-fail"); }
            catch (Exception e) { Log.Info($"pictures: no shared visuals, using DWM thumbnails: {e}"); }
        }
        return new ThumbnailPictures(overlay);
    }
}

/// DWM thumbnails (documented): DWM draws each window straight into the overlay at a rect we
/// give it. The camera costs one call into DWM per picture per frame, and DWM draws thumbnails
/// over the overlay's own content, so our UI can't sit on top of a picture without a window of
/// its own.
sealed class ThumbnailPictures(nint dest) : IPictures
{
    sealed class Picture
    {
        public nint Thumb;
        public RECT Dest;
        public bool Visible;
    }

    readonly Dictionary<nint, Picture> pictures = new();

    public string Name => "dwm-thumbnails";

    /// DWM draws its thumbnails over everything, so one of the desktop would cover the titles and
    /// rings on the backdrop: the backdrop's own copy of the desktop picture shows.
    public void SetDesktop(RECT? screen) { }
    public bool Lost => false; // DWM's own: registered afresh on each Attach
    public int Count => pictures.Count;

    public bool Attach(IEnumerable<CanvasWindow> bottomToTop)
    {
        int tried = 0;
        foreach (var w in bottomToTop) { tried++; Add(w); }
        return tried == 0 || pictures.Count > 0;
    }

    public bool Add(CanvasWindow w)
    {
        if (pictures.ContainsKey(w.Hwnd)) return true;
        int hr = Dwm.DwmRegisterThumbnail(dest, w.Hwnd, out nint thumb);
        if (hr != 0) { Log.Info($"picture: DwmRegisterThumbnail {w} failed 0x{hr:X8}"); return false; }
        pictures[w.Hwnd] = new Picture { Thumb = thumb };
        return true;
    }

    public void Remove(CanvasWindow w)
    {
        if (pictures.Remove(w.Hwnd, out var p)) Dwm.DwmUnregisterThumbnail(p.Thumb);
    }

    /// DWM stacks thumbnails in the order they're registered, so this registers it again.
    public void Raise(CanvasWindow w)
    {
        if (pictures.Remove(w.Hwnd, out var p)) Dwm.DwmUnregisterThumbnail(p.Thumb);
        Add(w);
    }

    public void Detach()
    {
        foreach (var p in pictures.Values) Dwm.DwmUnregisterThumbnail(p.Thumb);
        pictures.Clear();
    }

    /// Pictures wholly outside the view are hidden, and unchanged ones aren't sent again.
    public void Layout(IReadOnlyList<CanvasWindow> windows, Camera camera, RectD view)
    {
        foreach (var w in windows) Moved(w, camera, view);
    }

    public bool SetGuides(IReadOnlyList<(Vec a, Vec b)> lines, double thickness) => false;
    public bool DrawsDecorations => false;
    public void SetLabels(IReadOnlyList<Label> labels, double scale) { }
    public void SetMarks(Marks marks, double scale) { }
    public bool SetMinimap(MinimapDrawing? minimap, double scale) => false;
    public bool SetDesktops(DesktopsDrawing? desktops, double scale) => false;
    public void Present() { }

    /// The next Moved sends it again, with the new source rect.
    public void Resized(CanvasWindow w)
    {
        if (pictures.TryGetValue(w.Hwnd, out var p)) p.Dest = default;
    }

    public void Moved(CanvasWindow w, Camera camera, RectD view)
    {
        if (!pictures.TryGetValue(w.Hwnd, out var p)) return;
        var r = camera.ToView(w.Frame);
        bool visible = r.Intersects(view) && r.W >= 1 && r.H >= 1;
        var destRect = r.Round();
        if (visible == p.Visible && (!visible || Same(destRect, p.Dest))) return;
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Dwm.DWM_TNP_RECTDESTINATION | Dwm.DWM_TNP_RECTSOURCE | Dwm.DWM_TNP_VISIBLE |
                      Dwm.DWM_TNP_OPACITY | Dwm.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = destRect,
            rcSource = w.SourceRect,
            opacity = 255,
            fVisible = visible ? 1 : 0,
            fSourceClientAreaOnly = 0,
        };
        Dwm.DwmUpdateThumbnailProperties(p.Thumb, ref props);
        p.Dest = destRect;
        p.Visible = visible;
    }

    public void Flush() { }

    public void Dispose() => Detach();

    static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
}
