namespace Expanse;

public enum Direction { Left, Right, Up, Down }

/// The canvas maths, ported line for line from the Mac app (CanvasController.swift: nearest,
/// derive, separate, overlappingPairs, clusters, tidyUp, gravity, align, separateGroups,
/// freeSpot, snap) and checked against its Swift on random layouts (src/Layout.Tests). Canvas
/// space: top-left origin, y down.
///
/// Where the Swift's results depend on it, this keeps its behaviour: stable sorts (OrderBy),
/// the first of equal minimums or maximums (MinBy/MaxBy), rounding half away from zero, and
/// CGRect's intersects / insetBy (see RectD).
public static class Layout
{
    /// The gap between neighbouring windows: edge snapping, new-window placement and Tidy Up.
    public const double Gap = 20;
    /// Windows closer than this to a neighbour count as one group (cluster). Tidy Up tidies each
    /// group where it is, and keeps groups at least this far apart.
    public const double ClusterReach = 240;
    /// Edges closer than this to lining up get lined up by Tidy Up.
    public const double AlignReach = 60;

    /// Swift's `.rounded()`.
    public static double Round(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

    static IEnumerable<int> Indices<T>(IReadOnlyList<T> list) => Enumerable.Range(0, list.Count);

    // MARK: Spatial navigation

    /// The nearest window in `direction`. A window sharing the row (for left / right) or column
    /// (for up / down) wins outright, nearest first; failing that, the closest window within ~35°
    /// of straight that way. Windows mostly above or below never count as left or right.
    public static int? Nearest(Direction direction, RectD from, IReadOnlyList<RectD> frames)
    {
        // Overlap of at least a third of the smaller span.
        static bool Shared(double a0, double a1, double b0, double b1) =>
            Math.Min(a1, b1) - Math.Max(a0, b0) >= Math.Min(a1 - a0, b1 - b0) / 3;
        (int index, double score)? inLine = null, inCone = null;
        for (int i = 0; i < frames.Count; i++)
        {
            var r = frames[i];
            if (r == from) continue;
            double dx = r.MidX - from.MidX, dy = r.MidY - from.MidY;
            bool ahead, sameLine, withinCone;
            double gap;
            switch (direction)
            {
                case Direction.Right:
                    ahead = dx > 1 && r.X > from.X; gap = r.X - from.Right;
                    sameLine = Shared(r.Y, r.Bottom, from.Y, from.Bottom); withinCone = Math.Abs(dy) <= dx * 0.7;
                    break;
                case Direction.Left:
                    ahead = dx < -1 && r.Right < from.Right; gap = from.X - r.Right;
                    sameLine = Shared(r.Y, r.Bottom, from.Y, from.Bottom); withinCone = Math.Abs(dy) <= -dx * 0.7;
                    break;
                case Direction.Down:
                    ahead = dy > 1 && r.Y > from.Y; gap = r.Y - from.Bottom;
                    sameLine = Shared(r.X, r.Right, from.X, from.Right); withinCone = Math.Abs(dx) <= dy * 0.7;
                    break;
                default:
                    ahead = dy < -1 && r.Bottom < from.Bottom; gap = from.Y - r.Bottom;
                    sameLine = Shared(r.X, r.Right, from.X, from.Right); withinCone = Math.Abs(dx) <= -dy * 0.7;
                    break;
            }
            if (!ahead) continue;
            if (sameLine)
            {
                // Equal gaps (two windows snapped below one): the better-aligned one wins.
                double score = gap + r.Center.DistanceTo(from.Center) / 1000;
                if (score < (inLine?.score ?? double.PositiveInfinity)) inLine = (i, score);
            }
            else if (withinCone)
            {
                double d = r.Center.DistanceTo(from.Center);
                if (d < (inCone?.score ?? double.PositiveInfinity)) inCone = (i, d);
            }
        }
        return inLine?.index ?? inCone?.index;
    }

    // MARK: Fitting the layout to a screen

    /// Positions for windows of `sizes` that keep the `home` layout's gaps where you see them: each
    /// window keeps its distance to its nearest window on the left (and above), whether that's a
    /// snapped neighbour or the next group over, so shrinking windows pull the layout in. With
    /// home sizes this returns the home positions exactly. Callers push apart any overlap left.
    public static Vec[] Derive(IReadOnlyList<RectD> home, IReadOnlyList<SizeD> sizes)
    {
        // Distance between ranges; 0 when they overlap.
        static double Span(double a0, double a1, double b0, double b1) => Math.Max(0, Math.Max(a0, b0) - Math.Min(a1, b1));
        var x = home.Select(h => h.X).ToArray();
        var y = home.Select(h => h.Y).ToArray();
        foreach (int i in Indices(home).OrderBy(k => home[k].X).ToList())
        {
            var left = Indices(home).Where(k => k != i && home[k].Right <= home[i].X + 0.5).ToList();
            if (left.Count == 0) continue;
            // Nearest: the horizontal gap, with rows that don't line up counting extra.
            int j = left.MinBy(k => (home[i].X - home[k].Right) + 2 * Span(home[k].Y, home[k].Bottom, home[i].Y, home[i].Bottom));
            x[i] = x[j] + sizes[j].W + (home[i].X - home[j].Right);
        }
        foreach (int i in Indices(home).OrderBy(k => home[k].Y).ToList())
        {
            var above = Indices(home).Where(k => k != i && home[k].Bottom <= home[i].Y + 0.5).ToList();
            if (above.Count == 0) continue;
            int j = above.MinBy(k => (home[i].Y - home[k].Bottom) + 2 * Span(home[k].X, home[k].Right, home[i].X, home[i].Right));
            y[i] = y[j] + sizes[j].H + (home[i].Y - home[j].Bottom);
        }
        return x.Zip(y, (a, b) => new Vec(a, b)).ToArray();
    }

    /// Push overlapping frames apart in reading order. Only ever moving right or down means it
    /// always finishes.
    /// `keep`: pairs of windows (by index) left overlapping, see OverlappingPairs.
    public static void Separate(RectD[] frames, double gap, bool onlyOverlaps = false, ISet<(int, int)>? keep = null)
    {
        var order = Indices(frames).OrderBy(i => frames[i].Y).ThenBy(i => frames[i].X).ToList();
        var settled = new List<(int index, RectD frame)>();
        foreach (int i in order)
        {
            var r = frames[i];
            for (int step = 0; step < 200; step++)
            {
                double reach = onlyOverlaps ? 0.5 : -gap + 0.5;
                RectD? hit = null;
                foreach (var s in settled)
                {
                    if (keep != null && keep.Contains(Pair(s.index, i))) continue;
                    if (s.frame.Inset(reach, reach).Intersects(r)) { hit = s.frame; break; }
                }
                if (hit is not RectD h) break;
                double right = h.Right + gap - r.X;
                double down = h.Bottom + gap - r.Y;
                r = right <= down ? r with { X = r.X + right } : r with { Y = r.Y + down };
            }
            frames[i] = r;
            settled.Add((i, r));
        }
    }

    /// Two indices, unordered (the Swift IndexPair).
    public static (int, int) Pair(int i, int j) => (Math.Min(i, j), Math.Max(i, j));

    /// Pairs of frames that overlap.
    public static HashSet<(int, int)> OverlappingPairs(IReadOnlyList<RectD> frames)
    {
        var pairs = new HashSet<(int, int)>();
        for (int i = 0; i < frames.Count; i++)
            for (int j = i + 1; j < frames.Count; j++)
                if (frames[i].Inset(0.5, 0.5).Intersects(frames[j])) pairs.Add(Pair(i, j));
        return pairs;
    }

    /// Cluster number for each frame: frames within `reach` of each other (directly or through
    /// a chain of neighbours) share one.
    public static int[] Clusters(IReadOnlyList<RectD> frames, double reach)
    {
        var parent = Indices(frames).ToArray();
        int Root(int i) => parent[i] == i ? i : Root(parent[i]);
        for (int i = 0; i < frames.Count; i++)
            for (int j = i + 1; j < frames.Count; j++)
                if (frames[i].Inset(-reach / 2, -reach / 2).Intersects(frames[j].Inset(-reach / 2, -reach / 2)))
                    parent[Root(j)] = Root(i);
        return Indices(frames).Select(Root).ToArray();
    }

    // MARK: Tidy Up

    /// Tidy Up, on the home layout. Each group of windows is tidied where it is: overlaps pushed
    /// apart, edges that nearly line up lined up, and gaps closed to the standard gap. A group
    /// only moves (as a whole) when another group has grown into its space.
    public static void TidyUp(RectD[] frames)
    {
        var cluster = Clusters(frames, ClusterReach);
        var groups = Indices(frames).GroupBy(i => cluster[i]).Select(g => g.OrderBy(i => i).ToArray()).ToList();
        foreach (var g in groups)
        {
            var f = g.Select(i => frames[i]).ToArray();
            Separate(f, Gap);
            // Twice: closing a gap can undo a line-up, and lining up can open a gap.
            for (int pass = 0; pass < 2; pass++)
            {
                Gravity(f, Gap);
                Align(f, AlignReach, Gap);
            }
            Separate(f, Gap);
            for (int k = 0; k < g.Length; k++) frames[g[k]] = f[k];
        }
        SeparateGroups(frames, groups, ClusterReach);
    }

    static bool OverlapY(RectD a, RectD b) => Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y) > 0;
    static bool OverlapX(RectD a, RectD b) => Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X) > 0;

    /// Close gaps: each frame slides left (then up) until it sits `gap` from its nearest
    /// neighbour on that side. It only slides towards a neighbour it shares rows (or columns)
    /// with, so it never creates overlaps.
    public static void Gravity(RectD[] frames, double gap)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (int i in Indices(frames).OrderBy(k => frames[k].X).ToList())
            {
                var r = frames[i];
                var edges = Indices(frames).Where(k => k != i && frames[k].Right <= r.X + 0.5 && OverlapY(frames[k], r))
                    .Select(k => frames[k].Right).ToList();
                if (edges.Count > 0 && r.X - edges.Max() > gap) frames[i] = frames[i] with { X = edges.Max() + gap };
            }
            foreach (int i in Indices(frames).OrderBy(k => frames[k].Y).ToList())
            {
                var r = frames[i];
                var edges = Indices(frames).Where(k => k != i && frames[k].Bottom <= r.Y + 0.5 && OverlapX(frames[k], r))
                    .Select(k => frames[k].Bottom).ToList();
                if (edges.Count > 0 && r.Y - edges.Max() > gap) frames[i] = frames[i] with { Y = edges.Max() + gap };
            }
        }
    }

    /// Line up edges that nearly line up (within `reach`): a frame's top with its neighbour on the
    /// left, then its left edge with its neighbour above. Skipped where the move would bring it
    /// closer than `gap` to another frame (windows of different widths can't line up both sides).
    public static void Align(RectD[] frames, double reach, double gap)
    {
        bool Clear(RectD r, int except) =>
            !Indices(frames).Any(k => k != except && frames[k].Inset(-gap + 0.5, -gap + 0.5).Intersects(r));
        foreach (int i in Indices(frames).OrderBy(k => frames[k].X).ToList())
        {
            var r = frames[i];
            var beside = Indices(frames).Where(k =>
                k != i && frames[k].Right <= r.X + 0.5 && OverlapY(frames[k], r) && Math.Abs(frames[k].Y - r.Y) <= reach).ToList();
            if (beside.Count == 0) continue;
            int j = beside.MaxBy(k => frames[k].Right);
            if (frames[j].Y == r.Y) continue;
            var lined = r with { Y = frames[j].Y };
            if (Clear(lined, i)) frames[i] = lined;
        }
        foreach (int i in Indices(frames).OrderBy(k => frames[k].Y).ToList())
        {
            var r = frames[i];
            var above = Indices(frames).Where(k =>
                k != i && frames[k].Bottom <= r.Y + 0.5 && OverlapX(frames[k], r) && Math.Abs(frames[k].X - r.X) <= reach).ToList();
            if (above.Count == 0) continue;
            int j = above.MaxBy(k => frames[k].Bottom);
            if (frames[j].X == r.X) continue;
            var lined = r with { X = frames[j].X };
            if (Clear(lined, i)) frames[i] = lined;
        }
    }

    /// Keep groups `reach` apart, so they stay separate groups: a group that another has grown
    /// into is pushed away as a whole, right or down, in reading order.
    public static void SeparateGroups(RectD[] frames, IReadOnlyList<int[]> groups, double reach)
    {
        RectD Bounds(int[] g) => g.Skip(1).Aggregate(frames[g[0]], (b, k) => b.Union(frames[k]));
        var order = groups.OrderBy(g => Bounds(g).Y).ThenBy(g => Bounds(g).X).ToList();
        var settled = new List<RectD>();
        foreach (var g in order)
        {
            for (int step = 0; step < 200; step++)
            {
                Vec? push = null;
                foreach (var r in g.Select(k => frames[k]).ToList())
                {
                    RectD? hit = null;
                    foreach (var s in settled)
                        if (s.Inset(-reach + 0.5, -reach + 0.5).Intersects(r)) { hit = s; break; }
                    if (hit is not RectD h) continue;
                    double right = h.Right + reach - r.X, down = h.Bottom + reach - r.Y;
                    push = right <= down ? new Vec(right, 0) : new Vec(0, down);
                    break;
                }
                if (push is not Vec p) break;
                foreach (int k in g) frames[k] = frames[k].Offset(p);
            }
            settled.AddRange(g.Select(k => frames[k]));
        }
    }

    // MARK: New windows

    /// A canvas position for a window of `size` that overlaps none of `others`. First anywhere
    /// along an edge of `anchor`, the window it came from: right, below, left, then above, lined
    /// up with the anchor's top or left edge, then its bottom or right, then centred. Only when
    /// every edge is taken, beside the windows nearest `target`, the closest first. Spots in
    /// `visible` (the screen, in canvas space) win; with `onScreenOnly` they're the only ones.
    public static Vec? FreeSpot(SizeD size, RectD? anchor, IReadOnlyList<RectD> others, Vec target, RectD visible, bool onScreenOnly)
    {
        if (others.Count == 0) return null;
        const double gap = Gap;
        // (origin, score bias, ranked by distance to the anchor?)
        var candidates = new List<(Vec origin, double bias, bool byDistance)>();
        if (anchor is RectD a)
        {
            double[] ys = { a.Y, a.Bottom - size.H, a.MidY - size.H / 2 };
            double[] xs = { a.X, a.Right - size.W, a.MidX - size.W / 2 };
            for (int i = 0; i < 3; i++) candidates.Add((new Vec(a.Right + gap, ys[i]), i * 10, false));
            for (int i = 0; i < 3; i++) candidates.Add((new Vec(xs[i], a.Bottom + gap), 40 + i * 10, false));
            for (int i = 0; i < 3; i++) candidates.Add((new Vec(a.X - gap - size.W, ys[i]), 80 + i * 10, false));
            for (int i = 0; i < 3; i++) candidates.Add((new Vec(xs[i], a.Y - gap - size.H), 120 + i * 10, false));
        }
        // Further out: beside the windows nearest the anchor, after every spot touching it;
        // nudged by direction so ties go right, then down.
        double away = anchor == null ? 0 : 5000;
        foreach (var o in others.OrderBy(o => o.Center.DistanceTo(target)).Take(12).ToList())
        {
            candidates.Add((new Vec(o.Right + gap, o.Y), away, true));
            candidates.Add((new Vec(o.X, o.Bottom + gap), away + 40, true));
            candidates.Add((new Vec(o.X - gap - size.W, o.Y), away + 80, true));
            candidates.Add((new Vec(o.X, o.Y - gap - size.H), away + 120, true));
        }

        (Vec origin, double score)? best = null;
        foreach (var (origin, bias, byDistance) in candidates)
        {
            var r = new RectD(origin.X, origin.Y, size.W, size.H);
            if (others.Any(o => o.Inset(-gap / 2, -gap / 2).Intersects(r))) continue;
            if (onScreenOnly && !visible.Contains(r)) continue;
            double score = (byDistance ? r.Center.DistanceTo(target) : 0) + bias + (visible.Contains(r) ? 0 : 1500);
            if (score < (best?.score ?? double.PositiveInfinity)) best = (origin, score);
        }
        return best is { } b ? new Vec(Round(b.origin.X), Round(b.origin.Y)) : null;
    }

    // MARK: Snapping

    public readonly record struct GuideLine(Vec From, Vec To);

    readonly record struct Candidate(double Score, double Delta, double? Line, RectD? Span);

    /// Snap a dragged frame (one window, or a group's bounding box) to `others`. Edges and the
    /// standard gap snap firmly; centres snap softly (half the reach, and they lose ties) so they
    /// guide without fighting. A frame above or below a row of windows can also centre on the
    /// whole row, and one near `middle` (the screen's centre, in canvas space) on that. `scale`
    /// is the camera's: the reach is 10 screen pixels. Returns guide lines in canvas space.
    public static (RectD frame, List<GuideLine> lines) Snap(RectD f, IReadOnlyList<RectD> others, double scale, Vec middle)
    {
        double firm = 10 / scale;
        double soft = firm / 2;
        const double gap = Gap;
        Candidate? bestX = null, bestY = null;

        void Consider(double a, double b, double? line, ref Candidate? best, bool centre = false, RectD? span = null)
        {
            double d = b - a;
            if (!(Math.Abs(d) < (centre ? soft : firm))) return;
            double score = Math.Abs(d) + (centre ? soft / 2 : 0);
            if (score < (best?.Score ?? double.PositiveInfinity)) best = new Candidate(score, d, line, span);
        }
        foreach (var r in others)
        {
            foreach (double a in new[] { f.X, f.Right }) foreach (double b in new[] { r.X, r.Right }) Consider(a, b, b, ref bestX);
            Consider(f.MidX, r.MidX, r.MidX, ref bestX, centre: true);
            Consider(f.X, r.Right + gap, null, ref bestX);
            Consider(f.Right, r.X - gap, null, ref bestX);
            foreach (double a in new[] { f.Y, f.Bottom }) foreach (double b in new[] { r.Y, r.Bottom }) Consider(a, b, b, ref bestY);
            Consider(f.MidY, r.MidY, r.MidY, ref bestY, centre: true);
            Consider(f.Y, r.Bottom + gap, null, ref bestY);
            Consider(f.Bottom, r.Y - gap, null, ref bestY);
        }

        // The middle of the screen: a window dragged near it snaps to dead centre.
        var mid = middle;
        Consider(f.MidX, mid.X, mid.X, ref bestX, span: new RectD(mid.X, f.Y, 0, f.H));
        Consider(f.MidY, mid.Y, mid.Y, ref bestY, span: new RectD(f.X, mid.Y, f.W, 0));

        // Centre on a whole neighbouring row (above / below) or column (left / right).
        const double reach = ClusterReach;
        RectD wide = f.Inset(-reach, 0), tall = f.Inset(0, -reach);
        var below = others.Where(o => o.Y >= f.Bottom - 1 && o.Y - f.Bottom <= reach && o.Intersects(wide.Inset(0, -reach))).ToList();
        var above = others.Where(o => o.Bottom <= f.Y + 1 && f.Y - o.Bottom <= reach && o.Intersects(wide.Inset(0, -reach))).ToList();
        var left = others.Where(o => o.Right <= f.X + 1 && f.X - o.Right <= reach && o.Intersects(tall.Inset(-reach, 0))).ToList();
        var right = others.Where(o => o.X >= f.Right - 1 && o.X - f.Right <= reach && o.Intersects(tall.Inset(-reach, 0))).ToList();
        foreach (var row in new[] { below, above })
        {
            if (row.Count <= 1) continue;
            var u = row.Skip(1).Aggregate(row[0], (acc, r) => acc.Union(r));
            Consider(f.MidX, u.MidX, u.MidX, ref bestX, centre: true, span: u);
        }
        foreach (var column in new[] { left, right })
        {
            if (column.Count <= 1) continue;
            var u = column.Skip(1).Aggregate(column[0], (acc, r) => acc.Union(r));
            Consider(f.MidY, u.MidY, u.MidY, ref bestY, centre: true, span: u);
        }

        var outFrame = f;
        if (bestX is Candidate bx) outFrame = outFrame with { X = outFrame.X + bx.Delta };
        if (bestY is Candidate by) outFrame = outFrame with { Y = outFrame.Y + by.Delta };

        var lines = new List<GuideLine>();
        if (bestX?.Line is double x)
        {
            var span = bestX?.Span is RectD s ? s.Union(outFrame)
                : others.Where(o => new[] { o.X, o.MidX, o.Right }.Any(v => Math.Abs(v - x) < 0.5)).Append(outFrame)
                    .Aggregate(outFrame, (acc, r) => acc.Union(r));
            lines.Add(new GuideLine(new Vec(x, span.Y), new Vec(x, span.Bottom)));
        }
        if (bestY?.Line is double y)
        {
            var span = bestY?.Span is RectD s ? s.Union(outFrame)
                : others.Where(o => new[] { o.Y, o.MidY, o.Bottom }.Any(v => Math.Abs(v - y) < 0.5)).Append(outFrame)
                    .Aggregate(outFrame, (acc, r) => acc.Union(r));
            lines.Add(new GuideLine(new Vec(span.X, y), new Vec(span.Right, y)));
        }
        return (outFrame, lines);
    }
}
