using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static Expanse.Native.User32;

namespace Expanse;

/// The canvas as last saved: where each window lives, and the canvas point at the middle of the
/// screen. %LOCALAPPDATA%\Expanse\layout.json; a port of the Mac's LayoutStore.swift.
sealed class SavedLayout
{
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    /// The last run put windows back where they were before exiting (quit or pause).
    public bool CleanExit { get; set; }
    /// The last run handed over to a new one (run as administrator, or back): windows are where
    /// it left them, and its originals.json carries over.
    public bool HandOver { get; set; }
    /// The main screen's DPI it was saved at (0: not recorded). Spots and sizes are in its pixels.
    public uint Dpi { get; set; }
    /// The work area the layout was fitted to: at launch, windows only move by however much the
    /// screen differs (see Canvas.FitLayoutToScreen).
    public double? FittedWidth { get; set; }
    public double? FittedHeight { get; set; }
    public List<SavedWindow> Windows { get; set; } = new();
}

sealed class SavedWindow
{
    /// The app (the Mac's bundle ID): its exe's path, and the window's class.
    public string App { get; set; } = "";
    public string Class { get; set; } = "";
    public string AppName { get; set; } = "";
    /// A hash of the title (see LayoutStore.TitleKey).
    public string Title { get; set; } = "";
    /// Its canvas frame.
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    /// Its spot in the home layout (full chosen sizes), and the size chosen.
    public double? HX { get; set; }
    public double? HY { get; set; }
    public double? PW { get; set; }
    public double? PH { get; set; }
    /// The window handle and its process: while the app keeps running (Expanse restarted, say),
    /// they identify the window exactly, even among windows with the same title.
    public long Hwnd { get; set; }
    public uint Pid { get; set; }
}

static class LayoutStore
{
    static string File => Path.Combine(Log.Folder, "layout.json");
    static readonly JsonSerializerOptions json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static SavedLayout? Load()
    {
        try { return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<SavedLayout>(System.IO.File.ReadAllText(File), json) : null; }
        catch (Exception e) { Log.Info($"layout: couldn't read {File}: {e.Message}"); return null; }
    }

    /// Writes it if it changed since `last` (the JSON last written); returns what's on disk now.
    public static string? Save(SavedLayout layout, string? last)
    {
        string text = JsonSerializer.Serialize(layout, json);
        if (text == last) return last;
        try
        {
            Directory.CreateDirectory(Log.Folder);
            string temp = File + ".tmp";
            System.IO.File.WriteAllText(temp, text);
            System.IO.File.Move(temp, File, overwrite: true);
            return text;
        }
        catch (IOException e) { Log.Info($"layout: couldn't save: {e.Message}"); return last; }
    }

    /// Window titles can be private (document names, mail subjects), so the layout file stores
    /// a hash: enough to recognise the same window next time, not to read the title.
    public static string TitleKey(string title) =>
        title.Length == 0 ? "" : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(title))[..12]);

    /// Converts a layout saved at another scaling to `dpi`: spots spread from its middle, and sizes
    /// grow or shrink, by the ratio (see Canvas.ScaleCanvas).
    public static void ToDpi(SavedLayout layout, uint dpi)
    {
        if (layout.Dpi == 0 || layout.Dpi == dpi) { layout.Dpi = dpi; return; }
        double r = (double)dpi / layout.Dpi;
        foreach (var e in layout.Windows) Scale(e, new Vec(layout.CenterX, layout.CenterY), r);
        if (layout.FittedWidth is double fw && layout.FittedHeight is double fh)
            (layout.FittedWidth, layout.FittedHeight) = (Layout.Round(fw * r), Layout.Round(fh * r));
        Log.Info($"layout: saved at {layout.Dpi * 100 / 96}% scaling, now {dpi * 100 / 96}%: scaled by {r:0.###}");
        layout.Dpi = dpi;
    }

    /// One entry's spots spread from `c`, and its sizes, by `r`.
    public static void Scale(SavedWindow e, Vec c, double r)
    {
        double P(double v, double from) => Layout.Round(from + (v - from) * r);
        double Z(double v) => Layout.Round(v * r);
        (e.X, e.Y, e.W, e.H) = (P(e.X, c.X), P(e.Y, c.Y), Z(e.W), Z(e.H));
        if (e.HX is double hx) e.HX = P(hx, c.X);
        if (e.HY is double hy) e.HY = P(hy, c.Y);
        if (e.PW is double pw) e.PW = Z(pw);
        if (e.PH is double ph) e.PH = Z(ph);
    }

    public static SavedWindow Entry(CanvasWindow w) => new()
    {
        App = w.AppPath, Class = w.Class, AppName = w.AppName, Title = TitleKey(w.Title),
        X = w.Frame.X, Y = w.Frame.Y, W = w.Frame.W, H = w.Frame.H, Hwnd = w.Hwnd, Pid = w.Pid,
        HX = (w.Home ?? w.Frame.Origin).X, HY = (w.Home ?? w.Frame.Origin).Y,
        PW = w.Preferred.W > 0 ? w.Preferred.W : null, PH = w.Preferred.W > 0 ? w.Preferred.H : null,
    };

    // MARK: Snapshots

    /// Earlier layouts to go back to (Restore Layout on the tray menu), newest first. One is taken
    /// when the layout has changed and the newest is 15 minutes old, so they're minutes apart, not
    /// one per window move; only the newest three stay. As on the Mac.
    public sealed record Snapshot(DateTime Taken, string Path);
    public const int SnapshotsKept = 3;
    public static readonly TimeSpan SnapshotSpacing = TimeSpan.FromMinutes(15);

    static string SnapshotFolder => System.IO.Path.Combine(Log.Folder, "Snapshots");

    public static List<Snapshot> Snapshots()
    {
        if (!Directory.Exists(SnapshotFolder)) return new();
        // Named for the time they were taken (seconds since 1970).
        return Directory.GetFiles(SnapshotFolder, "*.json")
            .Select(f => long.TryParse(System.IO.Path.GetFileNameWithoutExtension(f), out long t)
                ? new Snapshot(DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime, f) : null)
            .OfType<Snapshot>().OrderByDescending(s => s.Taken).ToList();
    }

    public static void SaveSnapshot(SavedLayout layout)
    {
        try
        {
            Directory.CreateDirectory(SnapshotFolder);
            // Named for the second it's taken: two in the same second take the next free one.
            long t = DateTimeOffset.Now.ToUnixTimeSeconds();
            while (System.IO.File.Exists(System.IO.Path.Combine(SnapshotFolder, $"{t}.json"))) t++;
            string file = System.IO.Path.Combine(SnapshotFolder, $"{t}.json");
            System.IO.File.WriteAllText(file, JsonSerializer.Serialize(layout, json));
            foreach (var old in Snapshots().Skip(SnapshotsKept)) System.IO.File.Delete(old.Path);
        }
        catch (IOException e) { Log.Info($"layout: couldn't save a snapshot: {e.Message}"); }
    }

    public static SavedLayout? Load(Snapshot snapshot)
    {
        try { return JsonSerializer.Deserialize<SavedLayout>(System.IO.File.ReadAllText(snapshot.Path), json); }
        catch (Exception e) { Log.Info($"layout: couldn't read {snapshot.Path}: {e.Message}"); return null; }
    }

    /// "12 minutes ago (8:30 AM)".
    public static string Describe(Snapshot s)
    {
        var ago = DateTime.Now - s.Taken;
        static string N(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";
        string rel = ago.TotalMinutes < 1 ? "Just now"
            : ago.TotalHours < 1 ? N((int)ago.TotalMinutes, "minute")
            : ago.TotalDays < 1 ? N((int)ago.TotalHours, "hour")
            : N((int)ago.TotalDays, "day");
        return $"{rel} ({(s.Taken.Date == DateTime.Today ? s.Taken.ToString("t") : s.Taken.ToString("g"))})";
    }

    /// Whether that window still exists, in that process (handles get reused).
    static bool Exists(long hwnd, uint pid)
    {
        if (!IsWindow((nint)hwnd)) return false;
        GetWindowThreadProcessId((nint)hwnd, out uint p);
        return p == pid;
    }

    /// Picks the saved entry for a window: the same window (same handle in the same process),
    /// then same app and title, then same app. Matched entries are removed so two windows never
    /// claim the same slot, and `open` (windows open now) keeps the fallbacks from taking an
    /// entry that belongs to another open window, or one away (minimised) and due back.
    public static SavedWindow? TakeMatch(CanvasWindow w, ISet<(long, uint)> open, List<SavedWindow> pool)
    {
        int i = pool.FindIndex(e => e.Hwnd == w.Hwnd && e.Pid == w.Pid && SameApp(e, w));
        if (i < 0)
        {
            // A window far smaller than an entry (an update or loading box, a small dialog) doesn't
            // take it: the app's main window, a moment later, does (see LargerSpot).
            bool Free(SavedWindow e) => !open.Contains((e.Hwnd, e.Pid)) && !Exists(e.Hwnd, e.Pid) && !TooSmall(w.Frame.Size, e);
            string key = TitleKey(w.Title);
            i = pool.FindIndex(e => SameApp(e, w) && e.Title == key && Free(e));
            if (i < 0) i = pool.FindIndex(e => SameApp(e, w) && Free(e));
        }
        if (i < 0) return null;
        var match = pool[i];
        pool.RemoveAt(i);
        return match;
    }

    /// A free entry of the app's that `w` is too small to take (see TakeMatch): the window it's
    /// standing in for while the app starts up. Left in the pool.
    public static SavedWindow? LargerSpot(CanvasWindow w, ISet<(long, uint)> open, List<SavedWindow> pool) =>
        pool.FirstOrDefault(e => SameApp(e, w) && TooSmall(w.Frame.Size, e) && !open.Contains((e.Hwnd, e.Pid)) && !Exists(e.Hwnd, e.Pid));

    /// Under a quarter of the entry's area: not the same window coming back.
    static bool TooSmall(SizeD size, SavedWindow e) =>
        (e.PW ?? e.W) is double w && (e.PH ?? e.H) is double h && w > 0 && h > 0 && size.W * size.H < 0.25 * w * h;

    static bool SameApp(SavedWindow e, CanvasWindow w) =>
        string.Equals(e.App, w.AppPath, StringComparison.OrdinalIgnoreCase) && e.Class == w.Class;
}
