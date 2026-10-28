using Expanse.Native;

namespace Expanse;

/// Live window pictures on the overlay, laid out by the camera. Two ways to get them from DWM:
/// shared thumbnail visuals (the default) and plain DWM thumbnails (the fallback).
interface IPictures : IDisposable
{
    string Name { get; }
    int Count { get; }

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

    /// Positions every picture for `camera` (view = the overlay's client area).
    void Layout(IReadOnlyList<CanvasWindow> windows, Camera camera, RectD view);

    /// Waits until the last layout is on screen: before the overlay is shown, so it never
    /// appears without its pictures.
    void Flush();
}

static class Pictures
{
    /// Shared visuals unless `kind` is "dwm", or DirectComposition isn't available.
    public static IPictures Create(nint overlay, string kind)
    {
        if (kind != "dwm")
        {
            try { return new SharedVisualPictures(overlay, failForTesting: kind == "shared-fail"); }
            catch (Exception e) { Log.Info($"pictures: no shared visuals ({e.Message}); using DWM thumbnails"); }
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
    public int Count => pictures.Count;

    public bool Attach(IEnumerable<CanvasWindow> bottomToTop)
    {
        int tried = 0;
        foreach (var w in bottomToTop) { tried++; Add(w); }
        return tried == 0 || pictures.Count > 0;
    }

    public bool Add(CanvasWindow w)
    {
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
        foreach (var w in windows)
        {
            if (!pictures.TryGetValue(w.Hwnd, out var p)) continue;
            var r = camera.ToView(w.Frame);
            bool visible = r.Intersects(view) && r.W >= 1 && r.H >= 1;
            var destRect = r.Round();
            if (visible == p.Visible && (!visible || Same(destRect, p.Dest))) continue;
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
    }

    public void Flush() { }

    public void Dispose() => Detach();

    static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
}
