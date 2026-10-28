using Microsoft.Win32;

namespace Expanse;

/// What the minimap shows, in view coordinates: its panel, each window's block, the view's
/// rectangle (tinted and ringed), and the zoom level. See Minimap.
readonly record struct MinimapDrawing(RectD Panel, IReadOnlyList<RectD> Windows, RectD Viewport, bool FramesAll,
    string Zoom, bool Dark)
{
    public bool Same(MinimapDrawing o) =>
        Panel == o.Panel && Viewport == o.Viewport && FramesAll == o.FramesAll && Zoom == o.Zoom && Dark == o.Dark &&
        Windows.SequenceEqual(o.Windows);
}

/// The minimap, zoomed out at the bottom right, as on the Mac: every window as a block, where you
/// are as a tinted, ringed rectangle (a quiet line when it frames everything), the zoom level.
/// Click or drag in it to move the view there. The geometry is the Mac's (Minimap.swift), in
/// points times the screen's scaling.
sealed class Minimap
{
    public static readonly SizeD Size = new(208, 136);
    public const double Margin = 16, RingOutset = 3;

    /// Where the panel is (view coordinates), as last laid out.
    public RectD Panel { get; private set; }
    double mapScale = 1;
    Vec mapOrigin, mapOffset;
    RectD area;
    /// While you drag in it, the map holds still: the view moving would otherwise grow the map's
    /// bounds, rescale it, and send the same pointer spot further out each step.
    public bool Frozen;

    /// Lays the minimap out for `windows` and `viewport` (canvas coordinates) on a view of
    /// `viewSize`; `k` is the screen's scaling.
    public MinimapDrawing Update(IReadOnlyList<RectD> windows, RectD viewport, double zoom, Vec viewSize, double k)
    {
        double w = Size.W * k, h = Size.H * k;
        Panel = new RectD(viewSize.X - Margin * k - w, viewSize.Y - Margin * k - h, w, h);
        // Below a header row (the title, and the zoom as its value), as on the Mac.
        area = new RectD(Panel.X + 14 * k, Panel.Y + 32 * k, w - 28 * k, h - 44 * k);
        if (!Frozen)
        {
            var bounds = windows.Aggregate(viewport, (b, r) => b.Union(r));
            bounds = bounds.Inset(-bounds.W * 0.04, -bounds.H * 0.04);
            var fit = area.Inset((RingOutset + 1) * k, (RingOutset + 1) * k);
            mapScale = Math.Min(fit.W / bounds.W, fit.H / bounds.H);
            mapOrigin = bounds.Origin;
            mapOffset = new Vec(fit.X + (fit.W - bounds.W * mapScale) / 2, fit.Y + (fit.H - bounds.H * mapScale) / 2);
        }
        var v = ToMap(viewport);
        // Never so small it can't be found (zoomed in, among a lot of windows).
        double minW = 20 * k, minH = 13 * k;
        var shown = v.Inset(Math.Min(0, (v.W - minW) / 2), Math.Min(0, (v.H - minH) / 2));
        bool framesAll = shown.W * shown.H > 0.5 * area.W * area.H;
        return new MinimapDrawing(Panel, windows.Select(ToMap).ToList(), shown, framesAll, $"{Math.Round(zoom * 100)}%", dark);
    }

    RectD ToMap(RectD r) => new(mapOffset.X + (r.X - mapOrigin.X) * mapScale, mapOffset.Y + (r.Y - mapOrigin.Y) * mapScale,
        r.W * mapScale, r.H * mapScale);

    /// A point in the minimap (view coordinates, kept to the map) -> the canvas point it stands for.
    public Vec CanvasPoint(Vec p)
    {
        p = new Vec(Math.Clamp(p.X, area.X, area.Right), Math.Clamp(p.Y, area.Y, area.Bottom));
        return new(mapOrigin.X + (p.X - mapOffset.X) / mapScale, mapOrigin.Y + (p.Y - mapOffset.Y) / mapScale);
    }

    /// Windows' app theme (light or dark), which the panel follows: read at the start and when
    /// the theme changes (ThemeChanged).
    bool dark = IsDark();

    public void ThemeChanged() => dark = IsDark();

    static bool IsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
}
