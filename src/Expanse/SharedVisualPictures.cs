using System.Numerics;
using System.Runtime.InteropServices;
using Expanse.Native;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace Expanse;

/// Live pictures as DWM shared thumbnail visuals: DirectComposition visuals that DWM fills with
/// a window's content, placed in our own visual tree over the overlay. Every picture sits at its
/// canvas spot inside one `canvas` visual, so the camera is a single transform, applied by DWM
/// to all of them in the same frame, and our UI can later live in the same tree.
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

    readonly nint dest;
    readonly ID3D11Device d3d;
    readonly IDCompositionDesktopDevice device;
    readonly IDCompositionTarget target;
    readonly IDCompositionVisual2 root, canvas;
    readonly Dictionary<nint, Picture> pictures = new();
    readonly bool failForTesting;
    (double x, double y, double s)? lastCamera;

    public string Name => "shared-visuals";
    public int Count => pictures.Count;

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
        }
        // Topmost: the tree draws over the overlay's own (GDI) backdrop and labels.
        device.CreateTargetForHwnd(dest, true, out target);
        device.CreateVisual(out root);
        target.SetRoot(root);
        device.CreateVisual(out canvas);
        canvas.SetBitmapInterpolationMode(BitmapInterpolationMode.Linear);
        root.AddVisual(canvas, false, null);
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

    bool Add(CanvasWindow w)
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
        catch (EntryPointNotFoundException) { Log.Info("pictures: this Windows has no DwmpCreateSharedThumbnailVisual"); return false; }
        if (hr != 0 || visual == 0) { Log.Info($"pictures: shared visual for {w} failed 0x{hr:X8}"); return false; }
        var v = new IDCompositionVisual2(visual);
        v.SetOffsetX((float)w.Frame.X);
        v.SetOffsetY((float)w.Frame.Y);
        canvas.AddVisual(v, false, null); // no reference visual, not "above": on top of its siblings
        pictures[w.Hwnd] = new Picture(v, thumb);
        return true;
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
        device.Commit();
        foreach (var p in pictures.Values)
        {
            Dwm.DwmUnregisterThumbnail(p.Thumb);
            p.Visual.Dispose();
        }
        pictures.Clear();
    }

    /// One transform for the whole canvas; nothing else changes as the camera moves.
    public void Layout(IReadOnlyList<CanvasWindow> windows, Camera camera, RectD view)
    {
        var key = (camera.Origin.X, camera.Origin.Y, camera.Scale);
        if (lastCamera == key) return;
        float s = (float)camera.Scale;
        canvas.SetTransform(new Matrix3x2(s, 0, 0, s, (float)(-camera.Origin.X * camera.Scale), (float)(-camera.Origin.Y * camera.Scale)));
        device.Commit();
        lastCamera = key;
    }

    public void Flush() => device.WaitForCommitCompletion();

    public void Dispose()
    {
        Detach();
        canvas.Dispose();
        root.Dispose();
        target.Dispose();
        device.Dispose();
        d3d.Dispose();
    }
}
