using System.Diagnostics;
using System.Runtime.InteropServices;
using Expanse;
using Expanse.Native;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using static Expanse.Native.User32;

namespace Spikes;

/// Windows.Graphics.Capture: a stream of D3D textures per window. Used two ways here: as the
/// third picture route (does it work in the VM, what does it cost, is there a border), and as
/// a meter: a window's capture only gets a frame when the window repaints, so counting frames
/// says whether a parked window is still painting.
static class Capture
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, ref Guid iid);
        nint CreateForMonitor(nint monitor, ref Guid iid);
    }

    static readonly Guid ItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    static GraphicsCaptureItem ItemForWindow(nint hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = ItemIid;
        nint p = interop.CreateForWindow(hwnd, ref iid);
        try { return GraphicsCaptureItem.FromAbi(p); }
        finally { Marshal.Release(p); }
    }

    sealed class Stream : IDisposable
    {
        public readonly CanvasWindow Window;
        readonly Direct3D11CaptureFramePool pool;
        readonly GraphicsCaptureSession session;
        int frames;
        public string Border = "";

        public Stream(CanvasWindow w, IDirect3DDevice device)
        {
            Window = w;
            var item = ItemForWindow(w.Hwnd);
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            session = pool.CreateCaptureSession(item);
            try { session.IsBorderRequired = false; Border = "off"; }
            catch (Exception e) { Border = $"can't turn off: {e.Message}"; }
            try { session.IsCursorCaptureEnabled = false; } catch (Exception) { }
            pool.FrameArrived += (p, _) =>
            {
                using var f = p.TryGetNextFrame();
                Interlocked.Increment(ref frames);
            };
            session.StartCapture();
        }

        public int TakeFrames() => Interlocked.Exchange(ref frames, 0);

        public void Dispose()
        {
            session.Dispose();
            pool.Dispose();
        }
    }

    static (IDirect3DDevice device, ID3D11Device d3d) Device()
    {
        var d3d = D3D.Create();
        using var dxgi = d3d.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(D3D.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out nint p));
        var device = MarshalInterface<IDirect3DDevice>.FromAbi(p);
        Marshal.Release(p);
        return (device, d3d);
    }

    static string RequestBorderless()
    {
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) return "capture not supported";
            var status = Task.Run(async () => await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)).Result;
            return status.ToString();
        }
        catch (Exception e) { return $"failed: {e.Message}"; }
    }

    static ulong VideoMemory(ID3D11Device d3d)
    {
        using var dxgi = d3d.QueryInterface<IDXGIDevice>();
        dxgi.GetAdapter(out var adapter);
        using (adapter)
        using (var a3 = adapter.QueryInterface<IDXGIAdapter3>())
            return a3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage +
                   a3.QueryVideoMemoryInfo(0, MemorySegmentGroup.NonLocal).CurrentUsage;
    }

    static List<CanvasWindow> ScanWindows(RECT monitor)
    {
        var found = CanvasWindow.Scan();
        return found.Select(h =>
        {
            var w = new CanvasWindow(h);
            var f = w.ReadScreenFrame();
            w.Frame = new RectD(f.Left - monitor.Left, f.Top - monitor.Top, f.Width, f.Height);
            return w;
        }).ToList();
    }

    /// Captures every window for `seconds`: frames per window, memory, border.
    public static int RunBench(double seconds)
    {
        var (monitor, _) = PrimaryMonitor();
        string access = RequestBorderless();
        var (device, d3d) = Device();
        ulong mem0 = VideoMemory(d3d);
        long private0 = Process.GetCurrentProcess().PrivateMemorySize64;
        var streams = new List<Stream>();
        var failures = new List<string>();
        foreach (var w in ScanWindows(monitor))
        {
            try { streams.Add(new Stream(w, device)); }
            catch (Exception e) { failures.Add($"{w}: {e.Message}"); }
        }
        Thread.Sleep(1000); // first frames, buffers allocated
        foreach (var s in streams) s.TakeFrames();
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var dwm0 = PictureBench.DwmCpu();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        var cpu1 = Process.GetCurrentProcess().TotalProcessorTime;
        var dwm1 = PictureBench.DwmCpu();
        ulong mem1 = VideoMemory(d3d);
        long private1 = Process.GetCurrentProcess().PrivateMemorySize64;
        Out.Result("wgc", new
        {
            borderlessAccess = access,
            d3d = D3D.Driver,
            streams = streams.Count,
            failures,
            seconds,
            videoMemoryMB = (mem1 - (double)mem0) / 1048576,
            privateMemoryMB = (private1 - private0) / 1048576.0,
            cpuPercentOneCore = (cpu1 - cpu0).TotalMilliseconds / (seconds * 1000) * 100,
            dwmCpuPercentOneCore = dwm0 is TimeSpan a && dwm1 is TimeSpan b ? (b - a).TotalMilliseconds / (seconds * 1000) * 100 : (double?)null,
            perWindow = streams.Select(s => new { window = s.Window.ToString(), fps = s.TakeFrames() / seconds, border = s.Border }),
        });
        foreach (var s in streams) s.Dispose();
        return 0;
    }

    /// Which apps keep painting when parked off-screen. Opens a few apps showing something that
    /// changes by itself, then for each in turn (the others parked out of the way): on screen,
    /// fully off-screen, a 2 px strip at the screen's edge, and back. Frames per second from
    /// its capture in each spot, and whether Windows pulled it back.
    public static int RunParking(double seconds)
    {
        var (monitor, _) = PrimaryMonitor();
        string folder = Path.Combine(Out.Folder, "parking");
        Directory.CreateDirectory(folder);
        using var stop = new CancellationTokenSource();
        var started = StartSubjects(folder, stop.Token);
        Thread.Sleep(8000);

        var subjects = new List<(string name, CanvasWindow w)>();
        foreach (var w in ScanWindows(monitor))
        {
            string? name = w.Class == "CASCADIA_HOSTING_WINDOW_CLASS" ? "Windows Terminal"
                : w.Class == "CabinetWClass" && w.Title.Contains("parking") ? "File Explorer"
                : w.Title.Contains("Expanse tick") && w.App == "msedge" ? "Edge (Chromium)"
                : w.Title.Contains("tick.txt") && w.App == "Code" ? "VS Code (Electron)"
                : w.Title == "Source 1" ? "Win32 GDI window"
                : w.App == "Notepad" ? "Notepad"
                : null;
            if (name != null && subjects.All(s => s.name != name)) subjects.Add((name, w));
        }
        Out.Log($"parking: subjects {string.Join(", ", subjects.Select(s => $"{s.name} = {s.w}"))}");

        var (device, _) = Device();
        var originals = subjects.ToDictionary(s => s.w.Hwnd, s => { GetWindowRect(s.w.Hwnd, out var r); return r; });
        var results = new List<object>();
        foreach (var (name, w) in subjects)
        {
            // Everyone else out of the way, so the subject is never covered.
            foreach (var (_, other) in subjects.Where(s => s.w != w)) Move(other, monitor.Right + 4000, monitor.Top + 100);
            using var stream = new Stream(w, device);
            var row = new Dictionary<string, object> { ["app"] = name, ["window"] = w.ToString() };
            foreach (var (spot, x) in new[]
            {
                ("on screen", monitor.Left + 100),
                ("off screen", monitor.Right + 1000),
                ("2px strip", monitor.Right - 2),
                ("back on screen", monitor.Left + 100),
            })
            {
                Move(w, x, monitor.Top + 100);
                Thread.Sleep(1500);
                stream.TakeFrames();
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                double fps = stream.TakeFrames() / seconds;
                var f = Dwm.FrameBounds(w.Hwnd);
                bool pulledBack = f.Left != x;
                row[spot] = pulledBack ? $"{fps:0.#} fps (Windows moved it to {f.Left},{f.Top})" : $"{fps:0.#} fps";
            }
            results.Add(row);
            Out.Log($"parking: {string.Join(", ", row.Select(kv => $"{kv.Key}: {kv.Value}"))}");
        }
        foreach (var (h, r) in originals) SetWindowPos(h, 0, r.Left, r.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        Out.Result("parking", new { seconds, results });
        stop.Cancel();
        foreach (var p in started) { try { p.CloseMainWindow(); } catch (Exception) { } }
        return 0;
    }

    static void Move(CanvasWindow w, int frameX, int frameY)
    {
        w.ReadScreenFrame();
        SetWindowPos(w.Hwnd, 0, frameX - w.Insets.Left, frameY - w.Insets.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// Apps showing something that changes by itself: Edge on a page counting frames, VS Code
    /// on a file rewritten 10x a second, Windows Terminal printing the time, Explorer on a folder
    /// whose files churn, and one of our GDI source windows.
    static List<Process> StartSubjects(string folder, CancellationToken stop)
    {
        var started = new List<Process>();
        void Start(string file, string args)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
                if (p != null) started.Add(p);
            }
            catch (Exception e) { Out.Log($"parking: couldn't start {file}: {e.Message}"); }
        }

        string html = Path.Combine(folder, "tick.html");
        File.WriteAllText(html, """
            <!doctype html><title>Expanse tick</title>
            <body style="margin:0;background:#123;color:#fff;font:bold 20vh system-ui;display:grid;place-items:center;height:100vh">
            <div id=n>0</div><script>let n=0;(function f(){document.getElementById('n').textContent=++n;requestAnimationFrame(f)})()</script>
            """);
        Start("msedge", $"--new-window --no-first-run \"{new Uri(html).AbsoluteUri}\"");

        string tick = Path.Combine(folder, "tick.txt");
        File.WriteAllText(tick, "0");
        string code = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe");
        if (File.Exists(code)) Start(code, $"--new-window \"{tick}\"");

        Start("wt.exe", "-w new powershell -NoProfile -Command \"while(1){Get-Date -Format HH:mm:ss.fff; Start-Sleep -Milliseconds 100}\"");

        string churn = Path.Combine(folder, "parking");
        Directory.CreateDirectory(churn);
        Start("explorer.exe", $"\"{churn}\"");
        Start("notepad.exe", "");
        Start(Environment.ProcessPath!, "sources 1");

        new Thread(() =>
        {
            for (int i = 0; !stop.IsCancellationRequested; i++)
            {
                try
                {
                    File.WriteAllText(tick, $"{i}\n{DateTime.Now:HH:mm:ss.fff}");
                    if (i % 2 == 0)
                    {
                        File.WriteAllText(Path.Combine(churn, $"file {i % 20}.txt"), "x");
                        string old = Path.Combine(churn, $"file {(i + 10) % 20}.txt");
                        if (File.Exists(old)) File.Delete(old);
                    }
                }
                catch (IOException) { }
                Thread.Sleep(100);
            }
        }) { IsBackground = true }.Start();
        return started;
    }
}
