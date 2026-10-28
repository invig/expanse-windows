using System.Numerics;
using System.Runtime.InteropServices;
using Expanse.Native;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using AlphaMode = Vortice.DXGI.AlphaMode;
using BitmapInterpolationMode = Vortice.DirectComposition.BitmapInterpolationMode;
using BorderMode = Vortice.DirectComposition.BorderMode;
using Color4 = Vortice.Mathematics.Color4;

namespace Expanse;

/// Live pictures as DWM shared thumbnail visuals: DirectComposition visuals that DWM fills with
/// a window's content, placed in our own visual tree over the overlay. Every picture sits at its
/// canvas spot inside one `canvas` visual, so the camera is a single transform, applied by DWM
/// to all of them in the same frame. Our UI lives in the same tree, over the pictures: the hover
/// ring, titles (drawn with Direct2D/DirectWrite) and snapping guides.
///
/// Undocumented: dwmapi.dll ordinal 147 (see ADeltaX, "DWM Thumbnails. But with
/// IDCompositionVisual"), checked on Windows 11 build 26300. If it's missing or fails for every
/// window, the canvas falls back to DWM thumbnails.
sealed class SharedVisualPictures : IPictures
{
    [DllImport("dwmapi.dll", EntryPoint = "#147")]
    static extern int DwmpCreateSharedThumbnailVisual(nint dest, nint source, uint flags,
        ref DWM_THUMBNAIL_PROPERTIES props, nint dcompDevice, out nint visual, out nint thumb);

    [DllImport("dcomp.dll")]
    static extern int DCompositionCreateDevice3(nint renderingDevice, in Guid iid, out nint device);

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, int[]? levels,
        uint levelCount, uint sdkVersion, out nint device, out int level, out nint context);

    sealed class Picture(IDCompositionVisual2 visual, nint thumb)
    {
        public readonly IDCompositionVisual2 Visual = visual;
        public readonly nint Thumb = thumb;
    }

    /// A window's title: a visual with a surface drawn once per text and DPI scale.
    sealed class Title(IDCompositionVisual2 visual)
    {
        public readonly IDCompositionVisual2 Visual = visual;
        public IDCompositionSurface? Surface;
        public string Text = "";
        public double Scale;
        public float W, H;
    }

    readonly nint dest;
    readonly ID3D11Device d3d;
    readonly IDCompositionDesktopDevice device;
    readonly IDCompositionTarget target;
    readonly IDCompositionVisual2 root, canvas, titles;
    readonly Strips ring, guides;
    readonly ID2D1Factory1 d2dFactory;
    readonly ID2D1Device d2dDevice;
    readonly ID2D1DeviceContext d2d;
    readonly IDWriteFactory dwrite;
    readonly Dictionary<double, IDWriteTextFormat> fonts = new();
    readonly Dictionary<nint, Picture> pictures = new();
    readonly Dictionary<nint, Title> titleVisuals = new();
    readonly bool failForTesting;
    (double x, double y, double s)? lastCamera;

    public string Name => "shared-visuals";
    public int Count => pictures.Count;
    public bool DrawsDecorations => true;

    /// `failForTesting`: every picture fails, to exercise the fallback (`pictures shared-fail`).
    public SharedVisualPictures(nint destination, bool failForTesting = false)
    {
        dest = destination;
        this.failForTesting = failForTesting;
        d3d = CreateD3D(out string driver);
        using (var dxgi = d3d.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(DCompositionCreateDevice3(dxgi.NativePointer, typeof(IDCompositionDesktopDevice).GUID, out nint p));
            device = new IDCompositionDesktopDevice(p);
            d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded, DebugLevel.None);
            d2dDevice = d2dFactory.CreateDevice(dxgi);
        }
        d2d = d2dDevice.CreateDeviceContext();
        d2d.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale; // ClearType needs an opaque background
        dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);

        // Topmost: the tree draws over the overlay's own (GDI) backdrop. Bottom to top: the
        // pictures, the hover ring, titles, snapping guides.
        device.CreateTargetForHwnd(dest, true, out target);
        device.CreateVisual(out root);
        target.SetRoot(root);
        device.CreateVisual(out canvas);
        canvas.SetBitmapInterpolationMode(BitmapInterpolationMode.Linear);
        root.AddVisual(canvas, false, null);
        ring = new Strips(this, 76 / 255f, 154 / 255f, 1f);
        device.CreateVisual(out titles);
        root.AddVisual(titles, false, null);
        guides = new Strips(this, 1f, 0.22f, 0.37f);
        device.Commit();
        Log.Info($"pictures: shared visuals (D3D11 {driver})");
    }

    static ID3D11Device CreateD3D(out string driver)
    {
        const uint bgraSupport = 0x20;
        foreach (var (type, name) in new[] { (1, "hardware"), (5, "WARP") })
        {
            if (D3D11CreateDevice(0, type, 0, bgraSupport, null, 0, 7, out nint d, out int level, out nint context) != 0) continue;
            Marshal.Release(context);
            driver = $"{name}, feature level 0x{level:X}";
            return new ID3D11Device(d);
        }
        throw new InvalidOperationException("no D3D11 device");
    }

    // MARK: Pictures

    public bool Attach(IEnumerable<CanvasWindow> bottomToTop)
    {
        int tried = 0;
        foreach (var w in bottomToTop)
        {
            tried++;
            if (!Add(w) && pictures.Count == 0 && tried >= 3) break; // not working here: stop early
        }
        lastCamera = null;
        return tried == 0 || pictures.Count > 0;
    }

    public bool Add(CanvasWindow w)
    {
        if (pictures.ContainsKey(w.Hwnd)) return true;
        if (Create(w) is not Picture p) return false;
        canvas.AddVisual(p.Visual, false, null); // no reference visual, not "above": on top of its siblings
        pictures[w.Hwnd] = p;
        return true;
    }

    /// A new visual at the new size, put where the old one was in the stack.
    public void Resized(CanvasWindow w)
    {
        if (!pictures.TryGetValue(w.Hwnd, out var old) || Create(w) is not Picture p) return;
        canvas.AddVisual(p.Visual, true, old.Visual);
        canvas.RemoveVisual(old.Visual);
        Dwm.DwmUnregisterThumbnail(old.Thumb);
        old.Visual.Dispose();
        pictures[w.Hwnd] = p;
    }

    /// The window's picture: its visible frame, at its canvas spot.
    Picture? Create(CanvasWindow w)
    {
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Dwm.DWM_TNP_RECTDESTINATION | Dwm.DWM_TNP_RECTSOURCE | Dwm.DWM_TNP_VISIBLE |
                      Dwm.DWM_TNP_OPACITY | Dwm.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT(0, 0, (int)Math.Round(w.Frame.W), (int)Math.Round(w.Frame.H)),
            rcSource = w.SourceRect,
            opacity = 255,
            fVisible = 1,
        };
        int hr;
        nint visual = 0, thumb = 0;
        if (failForTesting) hr = unchecked((int)0x80004005);
        else try { hr = DwmpCreateSharedThumbnailVisual(dest, w.Hwnd, 2, ref props, device.NativePointer, out visual, out thumb); }
        catch (EntryPointNotFoundException) { Log.Info("pictures: this Windows has no DwmpCreateSharedThumbnailVisual"); return null; }
        if (hr != 0 || visual == 0) { Log.Info($"pictures: shared visual for {w} failed 0x{hr:X8}"); return null; }
        var v = new IDCompositionVisual2(visual);
        v.SetOffsetX((float)w.Frame.X);
        v.SetOffsetY((float)w.Frame.Y);
        return new Picture(v, thumb);
    }

    public void Remove(CanvasWindow w)
    {
        if (titleVisuals.Remove(w.Hwnd, out var t)) Dispose(t);
        if (!pictures.Remove(w.Hwnd, out var p)) return;
        canvas.RemoveVisual(p.Visual);
        device.Commit();
        Dwm.DwmUnregisterThumbnail(p.Thumb);
        p.Visual.Dispose();
    }

    public void Raise(CanvasWindow w)
    {
        if (!pictures.TryGetValue(w.Hwnd, out var p)) return;
        canvas.RemoveVisual(p.Visual);
        canvas.AddVisual(p.Visual, false, null);
        device.Commit();
    }

    public void Detach()
    {
        canvas.RemoveAllVisuals();
        titles.RemoveAllVisuals();
        ring.Set([]);
        guides.Set([]);
        device.Commit();
        foreach (var p in pictures.Values)
        {
            Dwm.DwmUnregisterThumbnail(p.Thumb);
            p.Visual.Dispose();
        }
        pictures.Clear();
        // Titles are kept: their surfaces are drawn again only when the text changes.
    }

    /// One transform for the whole canvas; nothing else changes as the camera moves.
    public void Layout(IReadOnlyList<CanvasWindow> windows, Camera camera, RectD view)
    {
        var key = (camera.Origin.X, camera.Origin.Y, camera.Scale);
        if (lastCamera == key) return;
        float s = (float)camera.Scale;
        canvas.SetTransform(new Matrix3x2(s, 0, 0, s, (float)(-camera.Origin.X * camera.Scale), (float)(-camera.Origin.Y * camera.Scale)));
        lastCamera = key;
    }

    public void Moved(CanvasWindow w, Camera camera, RectD view)
    {
        if (!pictures.TryGetValue(w.Hwnd, out var p)) return;
        p.Visual.SetOffsetX((float)w.Frame.X);
        p.Visual.SetOffsetY((float)w.Frame.Y);
    }

    public void Present()
    {
        var commit = device.Commit();
        if (commit.Failure) Log.Info($"pictures: commit failed {commit}");
    }

    public void Flush() => device.WaitForCommitCompletion();

    // MARK: Our UI over the pictures

    public bool SetGuides(IReadOnlyList<(Vec a, Vec b)> lines, double thickness)
    {
        guides.Set(lines.Select(l =>
        {
            // Vertical or horizontal: a thin rect along it, centred on the line.
            double x = Math.Min(l.a.X, l.b.X), y = Math.Min(l.a.Y, l.b.Y);
            double w = Math.Abs(l.b.X - l.a.X), h = Math.Abs(l.b.Y - l.a.Y);
            return w < h ? new RectD(x - thickness / 2, y, thickness, h) : new RectD(x, y - thickness / 2, w, thickness);
        }).ToList());
        return true;
    }

    /// A 3 px (at 100%) blue ring, 2 px outside the picture, as four strips.
    public void SetRing(RectD? picture, double scale)
    {
        if (picture is not RectD r) { ring.Set([]); return; }
        double t = Math.Max(2, Math.Round(3 * scale)), gap = Math.Max(1, Math.Round(2 * scale));
        var o = r.Inset(-(gap + t), -(gap + t));
        ring.Set([
            new RectD(o.X, o.Y, o.W, t), new RectD(o.X, o.Bottom - t, o.W, t),
            new RectD(o.X, o.Y + t, t, o.H - 2 * t), new RectD(o.Right - t, o.Y + t, t, o.H - 2 * t),
        ]);
    }

    /// Each title a dark pill, centred above its picture and no wider than it (cut off at the
    /// picture's width; a title only has to name the app).
    public void SetLabels(IReadOnlyList<Label> labels, double scale)
    {
        titles.RemoveAllVisuals();
        foreach (var label in labels)
        {
            if (!titleVisuals.TryGetValue(label.Hwnd, out var t))
            {
                device.CreateVisual(out var v);
                titleVisuals[label.Hwnd] = t = new Title(v);
            }
            if (t.Text != label.Text || t.Scale != scale) Draw(t, label.Text, scale);
            var r = label.Picture;
            float w = Math.Min(t.W, (float)r.W);
            float x = (float)Math.Round(r.X + (r.W - w) / 2), y = (float)Math.Round(r.Y - t.H - 6 * scale);
            t.Visual.SetOffsetX(x);
            t.Visual.SetOffsetY(y);
            t.Visual.SetClip(new Vortice.RawRectF(0, 0, w, t.H));
            titles.AddVisual(t.Visual, false, null);
        }
    }

    void Draw(Title t, string text, double scale)
    {
        if (!fonts.TryGetValue(scale, out var font))
        {
            font = dwrite.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, (float)(13 * scale));
            font.WordWrapping = WordWrapping.NoWrap;
            using var ellipsis = dwrite.CreateEllipsisTrimmingSign(font);
            font.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, ellipsis);
            fonts[scale] = font;
        }
        float pad = (float)Math.Round(10 * scale), h = (float)Math.Round(22 * scale);
        using var layout = dwrite.CreateTextLayout(text, font, (float)(480 * scale), h);
        float w = (float)Math.Ceiling(layout.Metrics.Width + pad * 2);

        device.CreateSurface((uint)w, (uint)h, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var surface);
        using (var dxgi = surface.BeginDraw<IDXGISurface>(null, out Vortice.Mathematics.Int2 offset))
        using (var bitmap = d2d.CreateBitmapFromDxgiSurface(dxgi, new BitmapProperties1(
                   new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                   BitmapOptions.Target | BitmapOptions.CannotDraw)))
        using (var pill = d2d.CreateSolidColorBrush(new Color4(32 / 255f, 32 / 255f, 34 / 255f, 1f)))
        using (var ink = d2d.CreateSolidColorBrush(new Color4(240 / 255f, 240 / 255f, 240 / 255f, 1f)))
        {
            d2d.Target = bitmap;
            d2d.BeginDraw();
            // The surface may be part of a bigger texture: draw only in our rectangle of it.
            d2d.Transform = Matrix3x2.CreateTranslation(offset.X, offset.Y);
            d2d.PushAxisAlignedClip(new Vortice.RawRectF(0, 0, w, h), AntialiasMode.Aliased);
            d2d.Clear(new Color4(0, 0, 0, 0));
            d2d.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(0, 0, w, h), h / 2, h / 2), pill);
            d2d.DrawTextLayout(new Vector2(pad, (h - layout.Metrics.Height) / 2), layout, ink);
            d2d.PopAxisAlignedClip();
            d2d.EndDraw();
            d2d.Target = null;
        }
        surface.EndDraw();
        t.Visual.SetContent(surface);
        t.Surface?.Dispose();
        t.Surface = surface;
        t.Text = text;
        t.Scale = scale;
        t.W = w;
        t.H = h;
    }

    static void Dispose(Title t)
    {
        t.Visual.Dispose();
        t.Surface?.Dispose();
    }

    /// Solid-colour rectangles over the pictures (the ring, the guides): visuals sharing one 1x1
    /// surface, each stretched to its rectangle by its transform.
    sealed class Strips
    {
        readonly SharedVisualPictures owner;
        readonly IDCompositionVisual2 container;
        readonly IDCompositionSurface colour;
        readonly List<IDCompositionVisual2> pool = new();

        public Strips(SharedVisualPictures owner, float r, float g, float b)
        {
            this.owner = owner;
            owner.device.CreateVisual(out container);
            owner.root.AddVisual(container, false, null);
            owner.device.CreateSurface(1, 1, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out colour);
            // ImmediateContext is cached by Vortice: not ours to dispose.
            using (var tex = colour.BeginDraw<ID3D11Texture2D>(null, out Vortice.Mathematics.Int2 offset))
            using (var rtv = owner.d3d.CreateRenderTargetView(tex, null))
            using (var context1 = owner.d3d.ImmediateContext.QueryInterface<ID3D11DeviceContext1>())
                context1.ClearView(rtv, new Color4(r, g, b, 1f), new[] { new Vortice.RawRect(offset.X, offset.Y, offset.X + 1, offset.Y + 1) });
            colour.EndDraw();
        }

        public void Set(IReadOnlyList<RectD> rects)
        {
            while (pool.Count < rects.Count)
            {
                owner.device.CreateVisual(out var v);
                v.SetContent(colour);
                v.SetBorderMode(BorderMode.Hard); // soft edges fade a stretched 1 px bitmap to nothing
                pool.Add(v);
            }
            container.RemoveAllVisuals();
            for (int i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                pool[i].SetTransform(new Matrix3x2((float)r.W, 0, 0, (float)r.H, (float)r.X, (float)r.Y));
                container.AddVisual(pool[i], false, null);
            }
        }

        public void Dispose()
        {
            container.RemoveAllVisuals();
            foreach (var v in pool) v.Dispose();
            container.Dispose();
            colour.Dispose();
        }
    }

    public void Dispose()
    {
        Detach();
        foreach (var t in titleVisuals.Values) Dispose(t);
        titleVisuals.Clear();
        ring.Dispose();
        guides.Dispose();
        foreach (var f in fonts.Values) f.Dispose();
        titles.Dispose();
        canvas.Dispose();
        root.Dispose();
        target.Dispose();
        device.Dispose();
        d2d.Dispose();
        d2dDevice.Dispose();
        d2dFactory.Dispose();
        dwrite.Dispose();
        d3d.Dispose();
    }
}
