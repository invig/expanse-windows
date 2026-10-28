using System.Text.Json;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// Where each window was before Expanse first moved it. Saved to a file as well, so that after
/// a crash (or `Expanse.exe --restore`) nothing is left stranded off-screen.
static class Originals
{
    sealed record Entry(long Hwnd, int X, int Y, string Title);

    static string File => Path.Combine(Log.Folder, "originals.json");

    public static void Save(IEnumerable<CanvasWindow> windows)
    {
        var entries = windows.Where(w => w.Original != null)
            .Select(w => new Entry(w.Hwnd, w.Original!.Value.Left, w.Original.Value.Top, w.Title)).ToList();
        try
        {
            Directory.CreateDirectory(Log.Folder);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(entries));
        }
        catch (IOException e) { Log.Info($"originals: couldn't save: {e.Message}"); }
    }

    public static void Restore(IEnumerable<CanvasWindow> windows)
    {
        int n = 0;
        foreach (var w in windows)
        {
            if (w.Original is not RECT r || !IsWindow(w.Hwnd)) continue;
            SetWindowPos(w.Hwnd, 0, r.Left, r.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            w.Original = null;
            w.Placed = null;
            n++;
        }
        Log.Info($"put {n} windows back where they were");
        try { System.IO.File.Delete(File); } catch (IOException) { }
    }

    /// After a crash: the file's windows that still exist go back.
    public static void RestoreFromFile()
    {
        if (!System.IO.File.Exists(File)) return;
        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(System.IO.File.ReadAllText(File)) ?? new();
            int n = 0;
            foreach (var e in entries)
            {
                if (!IsWindow((nint)e.Hwnd)) continue;
                SetWindowPos((nint)e.Hwnd, 0, e.X, e.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
                n++;
            }
            Log.Info($"restored {n} of {entries.Count} windows from the last run");
            System.IO.File.Delete(File);
        }
        catch (Exception e) { Log.Info($"originals: couldn't restore: {e.Message}"); }
    }
}
