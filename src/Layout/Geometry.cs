namespace Expanse;

public readonly record struct Vec(double X, double Y)
{
    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec operator *(Vec a, double s) => new(a.X * s, a.Y * s);
    public double DistanceTo(Vec p) => Math.Sqrt((X - p.X) * (X - p.X) + (Y - p.Y) * (Y - p.Y));
}

public readonly record struct SizeD(double W, double H);

/// A rectangle with CoreGraphics' CGRect semantics where the Mac app's maths depends on them:
/// edges that only touch don't intersect, and a rect inset past zero size is null and
/// intersects nothing.
public readonly record struct RectD(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double MidX => X + W / 2;
    public double MidY => Y + H / 2;
    public Vec Origin => new(X, Y);
    public Vec Center => new(MidX, MidY);
    public SizeD Size => new(W, H);
    public bool IsNull => W < 0 || H < 0;

    /// Half-open, for hit-testing a point.
    public bool Contains(Vec p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    /// `r` lies wholly inside (edges included).
    public bool Contains(RectD r) => !IsNull && !r.IsNull && r.X >= X && r.Y >= Y && r.Right <= Right && r.Bottom <= Bottom;

    public bool Intersects(RectD r) => !IsNull && !r.IsNull && r.X < Right && r.Right > X && r.Y < Bottom && r.Bottom > Y;

    /// Negative values grow it; past zero size it's null.
    public RectD Inset(double dx, double dy) => new(X + dx, Y + dy, W - dx * 2, H - dy * 2);

    public RectD Offset(Vec d) => new(X + d.X, Y + d.Y, W, H);

    /// x and y swapped, to run a horizontal layout rule vertically.
    public RectD Transposed => new(Y, X, H, W);

    public RectD Union(RectD r)
    {
        if (r.IsNull) return this;
        if (IsNull) return r;
        double x = Math.Min(X, r.X), y = Math.Min(Y, r.Y);
        return new(x, y, Math.Max(Right, r.Right) - x, Math.Max(Bottom, r.Bottom) - y);
    }

    public override string ToString() => $"{X:0},{Y:0} {W:0}x{H:0}";
}

/// Maps canvas space to view space (the overlay's client area: top-left origin, y down,
/// physical pixels). `Origin` is the canvas point shown at the view's top-left corner.
/// A straight port of Expanse's Camera (Sources/Expanse/Geometry.swift).
public struct Camera
{
    public Vec Origin;
    public double Scale;

    public const double MinScale = 0.03, MaxScale = 1.5;

    public Camera(Vec origin, double scale) { Origin = origin; Scale = scale; }

    public static Camera Identity => new(new Vec(0, 0), 1);

    public readonly Vec ToView(Vec p) => new((p.X - Origin.X) * Scale, (p.Y - Origin.Y) * Scale);
    public readonly RectD ToView(RectD r) { var o = ToView(r.Origin); return new(o.X, o.Y, r.W * Scale, r.H * Scale); }
    public readonly Vec ToCanvas(Vec p) => new(p.X / Scale + Origin.X, p.Y / Scale + Origin.Y);
    public readonly RectD ToCanvas(RectD r) { var o = ToCanvas(r.Origin); return new(o.X, o.Y, r.W / Scale, r.H / Scale); }

    /// Zooms so the canvas point under `anchor` (view coords) stays put.
    public void Zoom(double newScale, Vec anchor)
    {
        if (!double.IsFinite(newScale)) return;
        double s = Math.Clamp(newScale, MinScale, MaxScale);
        var c = ToCanvas(anchor);
        Scale = s;
        Origin = new Vec(c.X - anchor.X / s, c.Y - anchor.Y / s);
    }

    /// Moves the content by `d` view pixels.
    public void Pan(Vec d) => Origin = new Vec(Origin.X - d.X / Scale, Origin.Y - d.Y / Scale);

    public readonly Vec Center(Vec viewSize) => ToCanvas(new Vec(viewSize.X / 2, viewSize.Y / 2));

    public static Camera Centered(Vec c, double scale, Vec viewSize) =>
        new(new Vec(c.X - viewSize.X / 2 / scale, c.Y - viewSize.Y / 2 / scale), scale);

    public override readonly string ToString() => $"({Origin.X:0},{Origin.Y:0}) x{Scale:0.###}";
}
