using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Expanse;
using Expanse.Native;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Expanse.Native.User32;

namespace Spikes;

/// Every window as a live picture on a full-screen canvas, with the camera swinging between
/// the overview and 2.5x closer (one second each way, Expanse's ease-out), and a panel of our
/// own UI over the pictures. Measures frame pacing and the cost per frame.
///   dwm     DWM thumbnails (documented): one DwmUpdateThumbnailProperties per window per frame;
///           the UI panel is a second, layered window above the canvas.
///   shared  DwmpCreateSharedThumbnailVisual (undocumented, dwmapi #147): each thumbnail is a
///           DirectComposition visual in our own tree, so the camera is one transform per
///           frame and the UI panel is just another visual above.
static class PictureBench
{
    interface IPictures { int Count { get; } void Layout(Camera camera, RectD view); string Notes { get; } }

    static readonly WndProc canvasProc = (h, m, w, l) =>
    {
        if (m == WM_PAINT)
        {
            nint dc = BeginPaint(h, out var ps);
            GetClientRect(h, out var rc);
            nint b = Gdi32.CreateSolidBrush(Gdi32.RGB(28, 28, 30));
            FillRect(dc, ref rc, b);
            Gdi32.DeleteObject(b);
            EndPaint(h, ref ps);
            return 0;
        }
        return m == WM_ERASEBKGND ? 1 : DefWindowProc(h, m, w, l);
    };

    static readonly WndProc panelProc = (h, m, w, l) =>
    {
        if (m == WM_PAINT)
        {
            nint dc = BeginPaint(h, out var ps);
            GetClientRect(h, out var rc);
            nint b = Gdi32.CreateSolidBrush(Gdi32.RGB(26, 107, 242));
            FillRect(dc, ref rc, b);
            Gdi32.DeleteObject(b);
            Gdi32.SetBkMode(dc, Gdi32.TRANSPARENT);
            Gdi32.SetTextColor(dc, Gdi32.RGB(255, 255, 255));
            DrawText(dc, "UI over the pictures (layered window)", -1, ref rc, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
            EndPaint(h, ref ps);
            return 0;
        }
        return DefWindowProc(h, m, w, l);
    };

    public static int Run(string mode, double seconds)
    {
        var (monitor, _) = PrimaryMonitor();
        var viewSize = new Vec(monitor.Width, monitor.Height);
        var view = new RectD(0, 0, monitor.Width, monitor.Height);
        var found = CanvasWindow.Scan();
        found.Reverse(); // bottom-most first
        var windows = found.Select(h =>
        {
            var w = new CanvasWindow(h);
            var f = w.ReadScreenFrame();
            w.Frame = new RectD(f.Left - monitor.Left, f.Top - monitor.Top, f.Width, f.Height);
            return w;
        }).ToList();
        Out.Log($"pictures {mode}: {windows.Count} windows: {string.Join(", ", windows.Select(w => w.Title))}");

        var instance = RegisterWindowClass("ExpanseSpikeCanvas", canvasProc);
        long ex = WS_EX_TOPMOST | WS_EX_TOOLWINDOW | (mode == "shared" ? WS_EX_NOREDIRECTIONBITMAP : 0);
        nint canvas = CreateWindowEx((uint)ex, "ExpanseSpikeCanvas", "Spike canvas", unchecked((uint)WS_POPUP),
            monitor.Left, monitor.Top, monitor.Width, monitor.Height, 0, 0, instance, 0);
        IPictures pictures = mode switch
        {
            "dwm" => new DwmPictures(canvas, windows),
            "shared" => new SharedPictures(canvas, windows, monitor),
            _ => throw new ArgumentException("mode is dwm or shared"),
        };
        SetWindowPos(canvas, HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, monitor.Height, SWP_SHOWWINDOW);
        if (mode == "dwm")
        {
            RegisterWindowClass("ExpanseSpikePanel", panelProc);
            nint panel = CreateWindowEx((uint)(WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE),
                "ExpanseSpikePanel", "", unchecked((uint)WS_POPUP), 0, 0, 10, 10, 0, 0, instance, 0);
            SetLayeredWindowAttributes(panel, 0, 217, LWA_ALPHA);
            SetWindowPos(panel, HWND_TOPMOST, monitor.Right - 480, monitor.Bottom - 200, 440, 140, SWP_SHOWWINDOW | SWP_NOACTIVATE);
        }
        Pump();

        var overview = Fit(windows, viewSize, 0.6);
        var close = Camera.Centered(overview.Center(viewSize), overview.Scale * 2.5, viewSize);
        pictures.Layout(overview, view);
        Dwm.DwmFlush();

        var gaps = new List<double>();
        var layouts = new List<double>();
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var dwm0 = DwmCpu();
        long start = Stopwatch.GetTimestamp(), last = start;
        while (true)
        {
            Pump();
            double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
            if (elapsed >= seconds) break;
            int leg = (int)elapsed;
            double t = elapsed - leg, e = 1 - Math.Pow(1 - t, 3);
            var (from, to) = leg % 2 == 0 ? (overview, close) : (close, overview);
            var c0 = from.Center(viewSize);
            var c1 = to.Center(viewSize);
            double s = Math.Exp(Lerp(Math.Log(from.Scale), Math.Log(to.Scale), e));
            var camera = Camera.Centered(new Vec(Lerp(c0.X, c1.X, e), Lerp(c0.Y, c1.Y, e)), s, viewSize);
            long l0 = Stopwatch.GetTimestamp();
            pictures.Layout(camera, view);
            layouts.Add(Stopwatch.GetElapsedTime(l0).TotalMilliseconds);
            Dwm.DwmFlush();
            long now = Stopwatch.GetTimestamp();
            gaps.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
            last = now;
        }
        double wall = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var cpu1 = Process.GetCurrentProcess().TotalProcessorTime;
        var dwm1 = DwmCpu();
        gaps.Sort();
        Out.Result($"pictures-{mode}", new
        {
            mode,
            pictures = pictures.Count,
            notes = pictures.Notes,
            seconds = wall / 1000,
            frames = gaps.Count,
            fps = gaps.Count / (wall / 1000),
            gapMs = new { p50 = Out.Percentile(gaps, 0.5), p95 = Out.Percentile(gaps, 0.95), p99 = Out.Percentile(gaps, 0.99), max = gaps.LastOrDefault() },
            layoutMs = new { avg = layouts.DefaultIfEmpty().Average(), max = layouts.DefaultIfEmpty().Max() },
            cpuPercentOneCore = (cpu1 - cpu0).TotalMilliseconds / wall * 100,
            dwmCpuPercentOneCore = dwm0 is TimeSpan a && dwm1 is TimeSpan b ? (b - a).TotalMilliseconds / wall * 100 : (double?)null,
        });

        // Hold the overview a few seconds so a screenshot can be taken.
        pictures.Layout(overview, view);
        long hold = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(hold).TotalSeconds < 5) { Pump(); Thread.Sleep(16); }
        return 0;
    }

    static Camera Fit(List<CanvasWindow> windows, Vec viewSize, double maxScale)
    {
        if (windows.Count == 0) return Camera.Identity;
        var bounds = windows.Skip(1).Aggregate(windows[0].Frame, (b, w) => b.Union(w.Frame)).Inset(-80, -80);
        double s = Math.Min(Math.Min(viewSize.X / bounds.W, viewSize.Y / bounds.H), maxScale);
        return Camera.Centered(bounds.Center, Math.Max(s, Camera.MinScale), viewSize);
    }

    static double Lerp(double a, double b, double t) => a + (b - a) * t;

    static void Pump()
    {
        while (PeekMessage(out var m, 0, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref m);
            DispatchMessage(ref m);
        }
    }

    internal static TimeSpan? DwmCpu()
    {
        try { return Process.GetProcessesByName("dwm").FirstOrDefault()?.TotalProcessorTime; }
        catch (Exception) { return null; }
    }

    sealed class DwmPictures : IPictures
    {
        readonly List<(CanvasWindow w, nint thumb)> thumbs = new();
        readonly Dictionary<nint, (RECT dest, bool visible)> last = new();

        public DwmPictures(nint dest, List<CanvasWindow> windows)
        {
            foreach (var w in windows)
            {
                int hr = Dwm.DwmRegisterThumbnail(dest, w.Hwnd, out nint t);
                if (hr == 0) thumbs.Add((w, t)); else Out.Log($"DwmRegisterThumbnail {w}: 0x{hr:X8}");
            }
        }

        public int Count => thumbs.Count;
        public string Notes => "one DwmUpdateThumbnailProperties per moved picture per frame";

        public void Layout(Camera camera, RectD view)
        {
            foreach (var (w, thumb) in thumbs)
            {
                var r = camera.ToView(w.Frame);
                bool visible = r.Intersects(view);
                var dest = r.Round();
                if (last.TryGetValue(thumb, out var prev) && prev.visible == visible &&
                    (!visible || (prev.dest.Left == dest.Left && prev.dest.Top == dest.Top && prev.dest.Right == dest.Right && prev.dest.Bottom == dest.Bottom)))
                    continue;
                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = Dwm.DWM_TNP_RECTDESTINATION | Dwm.DWM_TNP_RECTSOURCE | Dwm.DWM_TNP_VISIBLE | Dwm.DWM_TNP_OPACITY,
                    rcDestination = dest, rcSource = w.SourceRect, opacity = 255, fVisible = visible ? 1 : 0,
                };
                Dwm.DwmUpdateThumbnailProperties(thumb, ref props);
                last[thumb] = (dest, visible);
            }
        }
    }

    sealed class SharedPictures : IPictures
    {
        [DllImport("dcomp.dll")]
        static extern int DCompositionCreateDevice3(nint renderingDevice, in Guid iid, out nint device);

        [DllImport("dwmapi.dll", EntryPoint = "#147")]
        static extern int DwmpCreateSharedThumbnailVisual(nint dest, nint source, uint flags,
            ref DWM_THUMBNAIL_PROPERTIES props, nint dcompDevice, out nint visual, out nint thumb);

        readonly ID3D11Device d3d;
        readonly IDCompositionDesktopDevice dcomp;
        readonly IDCompositionTarget target;
        readonly IDCompositionVisual2 root, canvas;
        readonly List<IDCompositionVisual2> visuals = new();

        public int Count => visuals.Count;
        public string Notes => $"one transform + Commit per frame; D3D11 {D3D.Driver}";

        public SharedPictures(nint hwnd, List<CanvasWindow> windows, RECT monitor)
        {
            d3d = D3D.Create();
            using (var dxgi = d3d.QueryInterface<IDXGIDevice>())
            {
                Marshal.ThrowExceptionForHR(DCompositionCreateDevice3(dxgi.NativePointer, typeof(IDCompositionDesktopDevice).GUID, out nint device));
                dcomp = new IDCompositionDesktopDevice(device);
            }
            dcomp.CreateTargetForHwnd(hwnd, true, out target);
            dcomp.CreateVisual(out root);
            target.SetRoot(root);
            dcomp.CreateVisual(out canvas);
            root.AddVisual(canvas, false, null);

            foreach (var w in windows)
            {
                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = Dwm.DWM_TNP_RECTDESTINATION | Dwm.DWM_TNP_RECTSOURCE | Dwm.DWM_TNP_VISIBLE |
                              Dwm.DWM_TNP_OPACITY | Dwm.DWM_TNP_SOURCECLIENTAREAONLY,
                    rcDestination = new RECT(0, 0, (int)w.Frame.W, (int)w.Frame.H),
                    rcSource = w.SourceRect, opacity = 255, fVisible = 1,
                };
                int hr = DwmpCreateSharedThumbnailVisual(hwnd, w.Hwnd, 2, ref props, dcomp.NativePointer, out nint vp, out _);
                if (hr != 0 || vp == 0) { Out.Log($"DwmpCreateSharedThumbnailVisual {w}: 0x{hr:X8}"); continue; }
                var v = new IDCompositionVisual2(vp);
                v.SetOffsetX((float)w.Frame.X);
                v.SetOffsetY((float)w.Frame.Y);
                canvas.AddVisual(v, true, null); // each above the last: bottom-most first
                visuals.Add(v);
            }

            // Our UI over the pictures, in the same tree: a translucent panel drawn with D3D.
            const int pw = 440, ph = 140;
            dcomp.CreateSurface(pw, ph, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var surface);
            using (var tex = surface.BeginDraw<ID3D11Texture2D>(null, out Int2 offset))
            using (var rtv = d3d.CreateRenderTargetView(tex, null))
            using (var context = d3d.ImmediateContext)
            using (var context1 = context.QueryInterface<ID3D11DeviceContext1>())
            {
                const float a = 0.85f; // premultiplied
                context1.ClearView(rtv, new Color4(0.10f * a, 0.42f * a, 0.95f * a, a),
                    new[] { new Vortice.RawRect(offset.X, offset.Y, offset.X + pw, offset.Y + ph) });
            }
            surface.EndDraw();
            dcomp.CreateVisual(out var panel);
            panel.SetContent(surface);
            panel.SetOffsetX(monitor.Width - 480);
            panel.SetOffsetY(monitor.Height - 200);
            root.AddVisual(panel, true, canvas);
            dcomp.Commit();
            Out.Log($"shared visuals: {visuals.Count} of {windows.Count}; D3D11 {D3D.Driver}");
        }

        public void Layout(Camera camera, RectD view)
        {
            float s = (float)camera.Scale;
            var m = new Matrix3x2(s, 0, 0, s, (float)(-camera.Origin.X * camera.Scale), (float)(-camera.Origin.Y * camera.Scale));
            canvas.SetTransform(m);
            dcomp.Commit();
        }
    }
}
