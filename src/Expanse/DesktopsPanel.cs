namespace Expanse;

/// What the Desktops panel shows, in view coordinates. See DesktopsPanel.
readonly record struct DesktopsDrawing(RectD Panel, IReadOnlyList<RectD> Tiles, IReadOnlyList<string> Names, int Current,
    int Hovered, bool Dark)
{
    public bool Same(DesktopsDrawing o) =>
        Panel == o.Panel && Current == o.Current && Hovered == o.Hovered && Dark == o.Dark &&
        Tiles.SequenceEqual(o.Tiles) && Names.SequenceEqual(o.Names);
}

/// Beside the minimap, zoomed out: one tile per virtual desktop, its name under it, the current
/// one ringed in the accent colour; click one to go there. Windows' nearest thing to the Mac's
/// Spaces panel (SpacesBar.swift, whose measurements these are), shown only when there's more than
/// one desktop.
sealed class DesktopsPanel
{
    public static readonly SizeD Tile = new(100, 63); // the screen's shape, roughly
    const double Gap = 14, Inset = 14, RingOutset = 4.5;

    List<VirtualDesktops.Desktop> desktops = new();
    int current = -1;
    long readAt;
    public RectD Panel { get; private set; }
    List<RectD> tiles = new();

    /// The desktops, read again if it's been half a second (Explorer says nothing when they change).
    public void Refresh()
    {
        if (System.Diagnostics.Stopwatch.GetElapsedTime(readAt).TotalSeconds < 0.5) return;
        readAt = System.Diagnostics.Stopwatch.GetTimestamp();
        (desktops, current) = VirtualDesktops.Read();
    }

    public int Count => desktops.Count;
    public int Current => current;

    /// Laid out to the left of `minimap` (its panel, view coordinates); null with a single desktop.
    public DesktopsDrawing? Update(RectD minimap, int hovered, double k, bool dark)
    {
        if (desktops.Count < 2) { Panel = default; tiles.Clear(); return null; }
        double w = (Inset * 2 + desktops.Count * (Tile.W + Gap) - Gap) * k, h = minimap.H;
        Panel = new RectD(minimap.X - 12 * k - w, minimap.Y, w, h);
        tiles = desktops.Select((_, i) => new RectD(Panel.X + (Inset + i * (Tile.W + Gap)) * k, Panel.Y + 37 * k, Tile.W * k, Tile.H * k)).ToList();
        return new DesktopsDrawing(Panel, tiles, desktops.Select(d => d.Name).ToList(), current, hovered, dark);
    }

    /// The tile at `p` (with the name under it), or -1.
    public int TileAt(Vec p)
    {
        for (int i = 0; i < tiles.Count; i++)
            if (new RectD(tiles[i].X - 4, tiles[i].Y - 4, tiles[i].W + 8, tiles[i].H + 26).Contains(p)) return i;
        return -1;
    }

    public static double Outset => RingOutset;
}
