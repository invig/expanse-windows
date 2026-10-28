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

    public static SavedWindow Entry(CanvasWindow w) => new()
    {
        App = w.AppPath, Class = w.Class, AppName = w.AppName, Title = TitleKey(w.Title),
        X = w.Frame.X, Y = w.Frame.Y, W = w.Frame.W, H = w.Frame.H, Hwnd = w.Hwnd, Pid = w.Pid,
    };

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
            bool Free(SavedWindow e) => !open.Contains((e.Hwnd, e.Pid)) && !Exists(e.Hwnd, e.Pid);
            string key = TitleKey(w.Title);
            i = pool.FindIndex(e => SameApp(e, w) && e.Title == key && Free(e));
            if (i < 0) i = pool.FindIndex(e => SameApp(e, w) && Free(e));
        }
        if (i < 0) return null;
        var match = pool[i];
        pool.RemoveAt(i);
        return match;
    }

    static bool SameApp(SavedWindow e, CanvasWindow w) =>
        string.Equals(e.App, w.AppPath, StringComparison.OrdinalIgnoreCase) && e.Class == w.Class;
}
