using Expanse.Native;

namespace Expanse;

/// Live window pictures on the overlay, as DWM thumbnails: DWM draws each window straight
/// into the overlay at a rect we give it, with no copying and no capture indicator. The cost:
/// DWM draws them over the overlay's own content, so anything of ours that must sit on top of
/// a picture needs a window of its own above the overlay.
sealed class Pictures
{
    sealed class Picture
    {
        public nint Thumb;
        public RECT Dest;
        public bool Visible;
    }

    readonly nint dest;
    readonly Dictionary<nint, Picture> pictures = new();

    public Pictures(nint destination) => dest = destination;

    public int Count => pictures.Count;

    /// Registers a picture for each window, bottom-most first: DWM stacks thumbnails in the
    /// order they're registered.
    public void Attach(IEnumerable<CanvasWindow> bottomToTop)
    {
        foreach (var w in bottomToTop) Add(w);
    }

    void Add(CanvasWindow w)
    {
        int hr = Dwm.DwmRegisterThumbnail(dest, w.Hwnd, out nint thumb);
        if (hr != 0) { Log.Info($"picture: DwmRegisterThumbnail {w} failed 0x{hr:X8}"); return; }
        pictures[w.Hwnd] = new Picture { Thumb = thumb };
    }

    /// Puts `w` on top of the other pictures (registered again, last).
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

    /// Positions every picture for `camera`. Pictures wholly outside the view are hidden, and
    /// unchanged ones aren't sent again: each update is a call into DWM.
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

    static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
}
