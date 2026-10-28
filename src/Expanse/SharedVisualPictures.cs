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

    /// A picture's own ring and close button, in the canvas right above it: a window in front
    /// covers them as it covers the picture. Canvas coordinates (see SetMarks).
    sealed class MarkLayer(IDCompositionVisual2 container, IDCompositionVisual2[] ring, IDCompositionVisual2 close)
    {
        public readonly IDCompositionVisual2 Container = container;
        public readonly IDCompositionVisual2[] Ring = ring;
        public readonly IDCompositionVisual2 Close = close;
        public bool Showing;
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
    readonly Strips accent, marqueeFill, guides;
    readonly Dictionary<nint, MarkLayer> markLayers = new();
    IDCompositionSurface? closeSurface;
    double closeScale;
    readonly IDCompositionVisual2 minimapVisual;
    IDCompositionSurface? minimapSurface;
    (int w, int h) minimapSize;
    MinimapDrawing? minimapDrawn;
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
        // pictures (each with its ring and close button), the selection box and marquee, titles,
        // snapping guides.
        device.CreateTargetForHwnd(dest, true, out target);
        device.CreateVisual(out root);
        target.SetRoot(root);
        device.CreateVisual(out canvas);
        canvas.SetBitmapInterpolationMode(BitmapInterpolationMode.Linear);
        root.AddVisual(canvas, false, null);
        accent = new Strips(this, 76 / 255f, 154 / 255f, 1f);
        marqueeFill = new Strips(this, 76 / 255f, 154 / 255f, 1f, 0.12f);
        device.CreateVisual(out titles);
        root.AddVisual(titles, false, null);
        guides = new Strips(this, 1f, 0.22f, 0.37f);
        device.CreateVisual(out minimapVisual);
        root.AddVisual(minimapVisual, false, null);
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
        var layer = NewMarkLayer();
        canvas.AddVisual(layer.Container, true, p.Visual);
        markLayers[w.Hwnd] = layer;
        return true;
    }

    MarkLayer NewMarkLayer()
    {
        device.CreateVisual(out var container);
        var ring = new IDCompositionVisual2[4];
        for (int i = 0; i < 4; i++)
        {
            device.CreateVisual(out ring[i]);
            ring[i].SetContent(accent.Colour);
            ring[i].SetBorderMode(BorderMode.Hard);
        }
        device.CreateVisual(out var close);
        return new MarkLayer(container, ring, close);
    }

    static void Dispose(MarkLayer layer)
    {
        layer.Container.RemoveAllVisuals();
        foreach (var v in layer.Ring) v.Dispose();
        layer.Close.Dispose();
        layer.Container.Dispose();
    }

    /// A new visual at the new size, put where the old one was in the stack (under its marks).
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
        if (markLayers.Remove(w.Hwnd, out var layer))
        {
            canvas.RemoveVisual(layer.Container);
            Dispose(layer);
        }
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
        if (markLayers.TryGetValue(w.Hwnd, out var layer))
        {
            canvas.RemoveVisual(layer.Container);
            canvas.AddVisual(layer.Container, true, p.Visual);
        }
        device.Commit();
    }

    public void Detach()
    {
        canvas.RemoveAllVisuals();
        titles.RemoveAllVisuals();
        accent.Set([]);
        marqueeFill.Set([]);
        guides.Set([]);
        device.Commit();
        foreach (var p in pictures.Values)
        {
            Dwm.DwmUnregisterThumbnail(p.Thumb);
            p.Visual.Dispose();
        }
        pictures.Clear();
        foreach (var layer in markLayers.Values) Dispose(layer);
        markLayers.Clear();
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

    /// Rings 2 px outside their pictures (3 px at 100% hovered, 4 selected), a 1 px box 8 px
    /// around a selection, the marquee (filled at 12%, outlined), close buttons. All in the accent
    /// blue, as strips.
    public void SetMarks(Marks marks, double scale)
    {
        var rects = new List<RectD>();
        double gap = Math.Max(1, Math.Round(2 * scale)), thin = Math.Max(1, Math.Round(scale));
        if (marks.SelectionBox is RectD box) Outline(rects, box.Inset(-8 * scale, -8 * scale), thin);
        if (marks.Marquee is RectD m) Outline(rects, m, thin);
        accent.Set(rects);
        marqueeFill.Set(marks.Marquee is RectD fill ? [fill] : []);

        // Each window's ring and close button go in its mark layer, in canvas coordinates: worked
        // out in view pixels (so thicknesses stay whole pixels), then taken back through the camera.
        if (marks.Closes.Count > 0 && (closeSurface == null || closeScale != scale)) DrawClose(scale);
        var (ox, oy, s) = lastCamera ?? (0, 0, 1);
        RectD ToCanvas(RectD v) => new(v.X / s + ox, v.Y / s + oy, v.W / s, v.H / s);
        var rings = marks.Rings.ToDictionary(r => r.hwnd);
        var closes = marks.Closes.ToDictionary(c => c.hwnd);
        foreach (var (hwnd, layer) in markLayers)
        {
            bool ring = rings.TryGetValue(hwnd, out var r), close = closes.TryGetValue(hwnd, out var c);
            if (!ring && !close && !layer.Showing) continue;
            layer.Container.RemoveAllVisuals();
            layer.Showing = ring || close;
            if (ring)
            {
                var strips = new List<RectD>(4);
                double t = Math.Max(2, Math.Round((r.selected ? 4 : 3) * scale));
                Outline(strips, r.picture.Inset(-(gap + t), -(gap + t)), t);
                for (int i = 0; i < 4; i++)
                {
                    var cr = ToCanvas(strips[i]);
                    layer.Ring[i].SetTransform(new Matrix3x2((float)cr.W, 0, 0, (float)cr.H, (float)cr.X, (float)cr.Y));
                    layer.Container.AddVisual(layer.Ring[i], false, null);
                }
            }
            if (close)
            {
                // Drawn at the screen's pixel size: scaled by the inverse of the camera's zoom.
                var cv = Marks.CloseRect(c.picture, scale);
                layer.Close.SetContent(closeSurface);
                layer.Close.SetTransform(new Matrix3x2((float)(1 / s), 0, 0, (float)(1 / s),
                    (float)(Math.Round(cv.X) / s + ox), (float)(Math.Round(cv.Y) / s + oy)));
                layer.Container.AddVisual(layer.Close, false, null);
            }
        }
    }

    /// Four strips `t` thick along the inside of `o`.
    static void Outline(List<RectD> rects, RectD o, double t)
    {
        rects.Add(new RectD(o.X, o.Y, o.W, t));
        rects.Add(new RectD(o.X, o.Bottom - t, o.W, t));
        rects.Add(new RectD(o.X, o.Y + t, t, o.H - 2 * t));
        rects.Add(new RectD(o.Right - t, o.Y + t, t, o.H - 2 * t));
    }

    /// The minimap, drawn with Direct2D into a surface of its own and redrawn only when what it
    /// shows changes. Colours after the Mac's: marks dark on a light panel, light on a dark one;
    /// the view in the accent colour.
    public bool SetMinimap(MinimapDrawing? minimap, double scale)
    {
        if (minimap is not MinimapDrawing m)
        {
            if (minimapDrawn != null) { minimapVisual.SetContent(null); minimapDrawn = null; }
            return true;
        }
        if (minimapDrawn is MinimapDrawing last && last.Same(m)) return true;
        int w = (int)Math.Ceiling(m.Panel.W), h = (int)Math.Ceiling(m.Panel.H);
        if (minimapSurface == null || minimapSize != (w, h))
        {
            minimapSurface?.Dispose();
            device.CreateSurface((uint)w, (uint)h, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var created);
            minimapSurface = created;
            minimapSize = (w, h);
        }
        float k = (float)scale;
        System.Drawing.RectangleF Local(RectD r) => new((float)(r.X - m.Panel.X), (float)(r.Y - m.Panel.Y), (float)r.W, (float)r.H);
        static float Radius(RectD r, float lo, float hi, double share) =>
            (float)Math.Min(Math.Min(hi, Math.Max(lo, Math.Min(r.W, r.H) * share)), Math.Min(r.W, r.H) / 2);
        var accent = new Color4(76 / 255f, 154 / 255f, 1f, 1f);
        using (var dxgi = minimapSurface.BeginDraw<IDXGISurface>(null, out Vortice.Mathematics.Int2 offset))
        using (var bitmap = d2d.CreateBitmapFromDxgiSurface(dxgi, new BitmapProperties1(
                   new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                   BitmapOptions.Target | BitmapOptions.CannotDraw)))
        using (var panel = d2d.CreateSolidColorBrush(m.Dark ? new Color4(0.12f, 0.12f, 0.13f, 0.9f) : new Color4(0.97f, 0.97f, 0.98f, 0.9f)))
        using (var edge = d2d.CreateSolidColorBrush(m.Dark ? new Color4(1f, 1f, 1f, 0.12f) : new Color4(0f, 0f, 0f, 0.1f)))
        using (var block = d2d.CreateSolidColorBrush(m.Dark ? new Color4(1f, 1f, 1f, 0.38f) : new Color4(0f, 0f, 0f, 0.2f)))
        using (var seam = d2d.CreateSolidColorBrush(m.Dark ? new Color4(0.12f, 0.12f, 0.13f, 0.5f) : new Color4(0f, 0f, 0f, 0.24f)))
        using (var tint = d2d.CreateSolidColorBrush(new Color4(accent.R, accent.G, accent.B, m.Dark ? 0.14f : 0.1f)))
        using (var ring = d2d.CreateSolidColorBrush(m.FramesAll ? (m.Dark ? new Color4(1f, 1f, 1f, 0.35f) : new Color4(0f, 0f, 0f, 0.28f)) : accent))
        using (var ink = d2d.CreateSolidColorBrush(m.Dark ? new Color4(1f, 1f, 1f, 1f) : new Color4(0f, 0f, 0f, 0.75f)))
        using (var titleFont = dwrite.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, 11 * k))
        using (var zoomFont = dwrite.CreateTextFormat("Segoe UI", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, 11 * k))
        {
            zoomFont.TextAlignment = TextAlignment.Trailing;
            d2d.Target = bitmap;
            d2d.BeginDraw();
            d2d.Transform = Matrix3x2.CreateTranslation(offset.X, offset.Y);
            d2d.PushAxisAlignedClip(new Vortice.RawRectF(0, 0, w, h), AntialiasMode.Aliased);
            d2d.Clear(new Color4(0, 0, 0, 0));
            float corner = 12 * k;
            var panelRect = new System.Drawing.RectangleF(0.5f, 0.5f, (float)m.Panel.W - 1, (float)m.Panel.H - 1);
            d2d.FillRoundedRectangle(new RoundedRectangle(panelRect, corner, corner), panel);
            d2d.DrawRoundedRectangle(new RoundedRectangle(panelRect, corner, corner), edge, 1);
            if (!m.FramesAll)
            {
                float vr = Radius(m.Viewport, 3 * k, 12 * k, 0.14);
                d2d.FillRoundedRectangle(new RoundedRectangle(Local(m.Viewport), vr, vr), tint);
            }
            foreach (var r in m.Windows)
            {
                float rr = Radius(r, 2 * k, 3 * k, 0.18);
                var rect = new RoundedRectangle(Local(r), rr, rr);
                d2d.FillRoundedRectangle(rect, block);
                d2d.DrawRoundedRectangle(rect, seam, m.Dark ? 0.75f * k : 0.5f * k);
            }
            // The ring stands off the windows inside it: a frame round what you see, not one more
            // (selected) window.
            var outer = m.Viewport.Inset(-Minimap.RingOutset * k, -Minimap.RingOutset * k);
            float outerRadius = Radius(m.Viewport, 3 * k, 12 * k, 0.14) + (float)(Minimap.RingOutset * k);
            d2d.DrawRoundedRectangle(new RoundedRectangle(Local(outer), outerRadius, outerRadius), ring, (m.FramesAll ? 1 : 2) * k);
            d2d.DrawText("Expanse", titleFont, new Vortice.RawRectF(14 * k, 9 * k, 134 * k, 25 * k), ink);
            d2d.DrawText(m.Zoom, zoomFont, new Vortice.RawRectF(w - 74 * k, 9 * k, w - 14 * k, 25 * k), ink);
            d2d.PopAxisAlignedClip();
            d2d.EndDraw();
            d2d.Target = null;
        }
        minimapSurface.EndDraw();
        minimapVisual.SetContent(minimapSurface);
        minimapVisual.SetOffsetX((float)Math.Round(m.Panel.X));
        minimapVisual.SetOffsetY((float)Math.Round(m.Panel.Y));
        minimapDrawn = m;
        return true;
    }

    /// The close button: a dark, three-quarters opaque circle with a white ×, as on the Mac.
    void DrawClose(double scale)
    {
        float size = (float)Math.Round(26 * scale), a = (float)(9 * scale), b = size - a;
        device.CreateSurface((uint)size, (uint)size, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var surface);
        using (var dxgi = surface.BeginDraw<IDXGISurface>(null, out Vortice.Mathematics.Int2 offset))
        using (var bitmap = d2d.CreateBitmapFromDxgiSurface(dxgi, new BitmapProperties1(
                   new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                   BitmapOptions.Target | BitmapOptions.CannotDraw)))
        using (var dark = d2d.CreateSolidColorBrush(new Color4(0.1f, 0.1f, 0.1f, 0.75f)))
        using (var white = d2d.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f)))
        using (var round = d2dFactory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round }))
        {
            d2d.Target = bitmap;
            d2d.BeginDraw();
            d2d.Transform = Matrix3x2.CreateTranslation(offset.X, offset.Y);
            d2d.PushAxisAlignedClip(new Vortice.RawRectF(0, 0, size, size), AntialiasMode.Aliased);
            d2d.Clear(new Color4(0, 0, 0, 0));
            d2d.FillEllipse(new Ellipse(new Vector2(size / 2, size / 2), size / 2, size / 2), dark);
            float w = (float)(2 * scale);
            d2d.DrawLine(new Vector2(a, a), new Vector2(b, b), white, w, round);
            d2d.DrawLine(new Vector2(a, b), new Vector2(b, a), white, w, round);
            d2d.PopAxisAlignedClip();
            d2d.EndDraw();
            d2d.Target = null;
        }
        surface.EndDraw();
        closeSurface?.Dispose();
        closeSurface = surface;
        closeScale = scale;
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
        public IDCompositionSurface Colour => colour;
        readonly List<IDCompositionVisual2> pool = new();

        public Strips(SharedVisualPictures owner, float r, float g, float b, float a = 1f)
        {
            this.owner = owner;
            owner.device.CreateVisual(out container);
            owner.root.AddVisual(container, false, null);
            owner.device.CreateSurface(1, 1, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out colour);
            // ImmediateContext is cached by Vortice: not ours to dispose.
            using (var tex = colour.BeginDraw<ID3D11Texture2D>(null, out Vortice.Mathematics.Int2 offset))
            using (var rtv = owner.d3d.CreateRenderTargetView(tex, null))
            using (var context1 = owner.d3d.ImmediateContext.QueryInterface<ID3D11DeviceContext1>())
                context1.ClearView(rtv, new Color4(r * a, g * a, b * a, a), new[] { new Vortice.RawRect(offset.X, offset.Y, offset.X + 1, offset.Y + 1) });
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
        accent.Dispose();
        marqueeFill.Dispose();
        guides.Dispose();
        closeSurface?.Dispose();
        minimapSurface?.Dispose();
        minimapVisual.Dispose();
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
