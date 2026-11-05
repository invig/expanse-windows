namespace Expanse;

/// A part of a screen you build on the canvas: a half or a quarter (or all of it). Its frame comes
/// from a screen-sized rectangle on the canvas (see Screens.Frame). Ported line for line from the
/// Mac app's Screens.swift, in the same order (Screens.Layout tries them in this order).
public enum Slot { Left, Right, TopLeft, TopRight, BottomLeft, BottomRight, Full }

public static class SlotExtensions
{
    /// For the log: "top-left".
    public static string Name(this Slot s) => s switch
    {
        Slot.Left => "left",
        Slot.Right => "right",
        Slot.TopLeft => "top-left",
        Slot.TopRight => "top-right",
        Slot.BottomLeft => "bottom-left",
        Slot.BottomRight => "bottom-right",
        _ => "full",
    };

    public static bool IsLeft(this Slot s) => s is Slot.Left or Slot.TopLeft or Slot.BottomLeft;
    public static bool IsTop(this Slot s) => s is Slot.TopLeft or Slot.TopRight;
    public static bool IsQuarter(this Slot s) => s is Slot.TopLeft or Slot.TopRight or Slot.BottomLeft or Slot.BottomRight;

    /// The half a quarter is part of.
    public static Slot Half(this Slot s) => s.IsQuarter() ? (s.IsLeft() ? Slot.Left : Slot.Right) : s;

    /// The half across from this slot's side: a window dropped here sends the one it was dropped
    /// on to the other side.
    public static Slot OtherHalf(this Slot s) => s.IsLeft() ? Slot.Right : Slot.Left;

    /// The quarters of a half, top first.
    public static Slot[] Quarters(this Slot s) => s switch
    {
        Slot.Left => [Slot.TopLeft, Slot.BottomLeft],
        Slot.Right => [Slot.TopRight, Slot.BottomRight],
        _ => [],
    };

    /// Whether the two slots cover any of the same part of the screen.
    public static bool Overlaps(this Slot a, Slot b)
    {
        if (a == b || a == Slot.Full || b == Slot.Full) return true;
        if (a.Half() != b.Half()) return false;
        // The same side: a half covers its quarters; two quarters don't meet.
        return !(a.IsQuarter() && b.IsQuarter());
    }
}

/// The edges of a window you're dragging to resize it (a corner is two).
[Flags]
public enum WindowEdges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

public static class WindowEdgesExtensions
{
    /// `start` with these edges moved by `d`, no smaller than `minimum` (the other edges stay).
    public static RectD Move(this WindowEdges edges, RectD start, Vec d, SizeD minimum)
    {
        double minX = start.X, maxX = start.Right, minY = start.Y, maxY = start.Bottom;
        if (edges.HasFlag(WindowEdges.Left)) minX = Math.Min(start.X + d.X, maxX - minimum.W);
        if (edges.HasFlag(WindowEdges.Right)) maxX = Math.Max(start.Right + d.X, minX + minimum.W);
        if (edges.HasFlag(WindowEdges.Top)) minY = Math.Min(start.Y + d.Y, maxY - minimum.H);
        if (edges.HasFlag(WindowEdges.Bottom)) maxY = Math.Max(start.Bottom + d.Y, minY + minimum.H);
        return new RectD(minX, minY, maxX - minX, maxY - minY);
    }
}

/// Which slots a screen of two, three or four windows has.
public sealed record ScreenLayout(string Title, Slot[] Slots)
{
    public static readonly ScreenLayout Halves = new("Side by side", [Slot.Left, Slot.Right]);
    public static readonly ScreenLayout LeftAndQuarters = new("One left, two right", [Slot.Left, Slot.TopRight, Slot.BottomRight]);
    public static readonly ScreenLayout QuartersAndRight = new("Two left, one right", [Slot.TopLeft, Slot.BottomLeft, Slot.Right]);
    public static readonly ScreenLayout Quarters = new("Four quarters", [Slot.TopLeft, Slot.TopRight, Slot.BottomLeft, Slot.BottomRight]);

    /// The layouts for `count` windows, the one to offer first first.
    public static ScreenLayout[] For(int count) => count switch
    {
        2 => [Halves],
        3 => [LeftAndQuarters, QuartersAndRight],
        4 => [Quarters],
        _ => [],
    };
}

/// The geometry of screens you build: the gap goes between slots and round the edge, so a built
/// screen is windows a gap apart whose outline fits the screen, which is what a screen group is
/// (see Layout.ScreenGroups).
public static class Screens
{
    /// `slot`'s frame in a screen-sized rectangle on the canvas, in whole pixels.
    public static RectD Frame(Slot slot, RectD screen, double gap)
    {
        var inner = screen.Inset(gap, gap);
        double leftWidth = Math.Floor((inner.W - gap) / 2), topHeight = Math.Floor((inner.H - gap) / 2);
        double rightX = inner.X + leftWidth + gap, bottomY = inner.Y + topHeight + gap;
        var left = new RectD(inner.X, inner.Y, leftWidth, inner.H);
        var right = new RectD(rightX, inner.Y, inner.Right - rightX, inner.H);
        return slot switch
        {
            Slot.Full => inner,
            Slot.Left => left,
            Slot.Right => right,
            Slot.TopLeft => new RectD(left.X, inner.Y, left.W, topHeight),
            Slot.TopRight => new RectD(right.X, inner.Y, right.W, topHeight),
            Slot.BottomLeft => new RectD(left.X, bottomY, left.W, inner.Bottom - bottomY),
            _ => new RectD(right.X, bottomY, right.W, inner.Bottom - bottomY),
        };
    }

    /// Where a screen goes so a window with `frame` stays where it is once it's in `slot`.
    public static Vec ScreenOrigin(RectD frame, Slot slot, SizeD size, double gap)
    {
        var offset = Frame(slot, new RectD(0, 0, size.W, size.H), gap).Origin;
        return new Vec(frame.X - offset.X, frame.Y - offset.Y);
    }

    /// The screen a group of windows was built as, and the slot each window is in: null if it isn't
    /// one. Each window's top-left is on its slot's (within 4 px), and it's no bigger than the slot
    /// and at most `slack` smaller (apps round sizes: a terminal to character cells).
    public static (RectD screen, Slot[] slots)? LayoutOf(IReadOnlyList<RectD> frames, SizeD size, double gap, double slack = 32)
    {
        if (frames.Count <= 1) return null;
        var outline = frames.Skip(1).Aggregate(frames[0], (b, f) => b.Union(f));
        // The screen's top-left lines up with the outline's top or left unless the top row or left
        // column is empty: try each corner of the outline.
        double[] xs = [outline.X - gap, outline.Right + gap - size.W];
        double[] ys = [outline.Y - gap, outline.Bottom + gap - size.H];
        foreach (double x in xs)
            foreach (double y in ys)
            {
                var screen = new RectD(x, y, size.W, size.H);
                var slots = new List<Slot>();
                foreach (var f in frames)
                {
                    Slot? found = null;
                    foreach (Slot s in Enum.GetValues<Slot>())
                        if (s != Slot.Full && !slots.Any(t => t.Overlaps(s)) && Fits(f, Frame(s, screen, gap), slack)) { found = s; break; }
                    if (found is not Slot slot) break;
                    slots.Add(slot);
                }
                if (slots.Count == frames.Count) return (screen, slots.ToArray());
            }
        return null;
    }

    /// The slot a window with `frame` is in, in the screen at `screen` (as LayoutOf judges it):
    /// null if it's in none.
    public static Slot? SlotOf(RectD frame, RectD screen, double gap, double slack = 32)
    {
        foreach (Slot s in Enum.GetValues<Slot>())
            if (s != Slot.Full && Fits(frame, Frame(s, screen, gap), slack)) return s;
        return null;
    }

    /// The screen tool's slot under `p`: a quarter near the top or bottom of a half, else the half.
    public static Slot? ToolSlot(Vec p, RectD screen)
    {
        if (!screen.Contains(p)) return null;
        bool left = p.X < screen.MidX;
        double y = (p.Y - screen.Y) / screen.H;
        if (y < 0.3) return left ? Slot.TopLeft : Slot.TopRight;
        if (y > 0.7) return left ? Slot.BottomLeft : Slot.BottomRight;
        return left ? Slot.Left : Slot.Right;
    }

    /// Putting a window in `slot` of a screen whose windows are in `taken`: where each of those
    /// goes. One in the half a quarter is part of moves to its other quarter; one already in the
    /// slot, or in a half you put a window in whole, leaves the screen (null). The rest stay.
    public static Slot?[] MakeWay(Slot slot, IReadOnlyList<Slot> taken) => taken.Select(s =>
    {
        if (!s.Overlaps(slot)) return s;
        if (slot.IsQuarter() && s == slot.Half())
            foreach (var q in slot.Half().Quarters())
                if (q != slot) return q;
        return (Slot?)null;
    }).ToArray();

    static bool Fits(RectD f, RectD slot, double slack) =>
        Math.Abs(f.X - slot.X) <= 4 && Math.Abs(f.Y - slot.Y) <= 4
        && f.W <= slot.W + 2 && f.H <= slot.H + 2
        && f.W >= slot.W - slack && f.H >= slot.H - slack;

    /// Which slot each window goes in, by where they are now (their centres): the assignment that
    /// keeps them most where they are relative to each other.
    public static Slot[] Assign(IReadOnlyList<Vec> centres, ScreenLayout layout)
    {
        if (centres.Count != layout.Slots.Length || centres.Count == 0) return layout.Slots;
        // Both sides in 0...1: the windows' spread, and the slots' places in a screen.
        Vec lo = centres[0], hi = centres[0];
        foreach (var c in centres)
        {
            lo = new Vec(Math.Min(lo.X, c.X), Math.Min(lo.Y, c.Y));
            hi = new Vec(Math.Max(hi.X, c.X), Math.Max(hi.Y, c.Y));
        }
        double sw = Math.Max(hi.X - lo.X, 1), sh = Math.Max(hi.Y - lo.Y, 1);
        var points = centres.Select(c => new Vec((c.X - lo.X) / sw, (c.Y - lo.Y) / sh)).ToArray();
        var unit = new RectD(0, 0, 1, 1);
        var targets = layout.Slots.Select(s => Frame(s, unit, 0).Center).ToArray();
        var best = layout.Slots;
        double bestCost = double.PositiveInfinity;
        foreach (var order in Permutations(Enumerable.Range(0, layout.Slots.Length).ToList()))
        {
            double cost = 0;
            for (int i = 0; i < points.Length; i++)
            {
                var t = targets[order[i]];
                cost += (points[i].X - t.X) * (points[i].X - t.X) + (points[i].Y - t.Y) * (points[i].Y - t.Y);
            }
            if (cost < bestCost - 0.0001) { bestCost = cost; best = order.Select(k => layout.Slots[k]).ToArray(); }
        }
        return best;
    }

    static IEnumerable<List<int>> Permutations(List<int> a)
    {
        if (a.Count <= 1) { yield return a; yield break; }
        for (int i = 0; i < a.Count; i++)
        {
            var rest = new List<int>(a);
            int x = rest[i];
            rest.RemoveAt(i);
            foreach (var p in Permutations(rest)) yield return [x, .. p];
        }
    }

    /// The drop zone at `p` over a window at `target`: a quarter in a corner (the outer 30% both
    /// ways), a half at a side (the outer 35%), nothing in the middle (an ordinary move).
    public static Slot? DropZone(Vec p, RectD target)
    {
        if (target.W <= 0 || target.H <= 0 || !target.Contains(p)) return null;
        double x = (p.X - target.X) / target.W, y = (p.Y - target.Y) / target.H;
        bool? side = x < 0.3 ? true : x > 0.7 ? false : null;
        bool? top = y < 0.3 ? true : y > 0.7 ? false : null;
        if (side is bool s && top is bool t)
            return s ? (t ? Slot.TopLeft : Slot.BottomLeft) : (t ? Slot.TopRight : Slot.BottomRight);
        if (x < 0.35) return Slot.Left;
        if (x > 0.65) return Slot.Right;
        return null;
    }
}
