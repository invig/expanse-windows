using Expanse.Native;

namespace Expanse;

/// Between the canvas's geometry (src/Layout) and Win32's RECT.
static class Conversions
{
    public static RECT Round(this RectD r) =>
        new((int)Layout.Round(r.X), (int)Layout.Round(r.Y), (int)Layout.Round(r.Right), (int)Layout.Round(r.Bottom));

    public static RectD ToRectD(this RECT r) => new(r.Left, r.Top, r.Width, r.Height);
}
