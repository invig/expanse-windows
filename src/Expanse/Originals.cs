using System.Text.Json;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// Where each window was before Expanse first moved it. Saved to a file as well, so that after
/// a crash (or `Expanse.exe --restore`) nothing is left stranded off-screen.
static class Originals
{
    /// `Maximised`: restored by Expanse to be moved (see CanvasWindow.Maximised), so it's
    /// maximised again rather than moved back.
    sealed record Entry(long Hwnd, int X, int Y, string Title, bool Maximised = false);

    static string File => Path.Combine(Log.Folder, "originals.json");

    public static void Save(IEnumerable<CanvasWindow> windows)
    {
        var entries = windows.Where(w => w.Original != null)
            .Select(w => new Entry(w.Hwnd, w.Original!.Value.Left, w.Original.Value.Top, w.Title, w.RestoredByUs)).ToList();
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
            int x, y; // where its visible frame goes
            if (w.RestoredByUs || IsZoomed(w.Hwnd))
            {
                // Maximised: maximised again, or still (it stays put: moving one does nothing).
                if (w.RestoredByUs) w.Maximise();
                var f = Dwm.FrameBounds(w.Hwnd);
                (x, y) = (f.Left, f.Top);
            }
            else
            {
                SetWindowPos(w.Hwnd, 0, r.Left, r.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
                (x, y) = (r.Left + w.Insets.Left, r.Top + w.Insets.Top);
            }
            // Its dialogs keep their place by it.
            foreach (var o in w.Owned.Where(o => IsWindow(o.Hwnd)))
                SetWindowPos(o.Hwnd, 0, x + (int)Math.Round(o.Frame.X - w.Frame.X) - o.Insets.Left,
                    y + (int)Math.Round(o.Frame.Y - w.Frame.Y) - o.Insets.Top, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            w.Original = null;
            w.Placed = null;
            n++;
        }
        Log.Info($"put {n} windows back where they were");
        try { System.IO.File.Delete(File); } catch (IOException) { }
    }

    /// Handed over from another Expanse: where its windows were before it moved them becomes
    /// ours, and the file stays.
    public static void Adopt(IEnumerable<CanvasWindow> windows)
    {
        if (!System.IO.File.Exists(File)) return;
        try
        {
            var entries = (JsonSerializer.Deserialize<List<Entry>>(System.IO.File.ReadAllText(File)) ?? new())
                .ToDictionary(e => (nint)e.Hwnd);
            int n = 0;
            foreach (var w in windows)
            {
                if (!entries.TryGetValue(w.Hwnd, out var e)) continue;
                w.Original = new RECT(e.X, e.Y, e.X, e.Y);
                if (e.Maximised) (w.Maximised, w.RestoredByUs) = (true, true);
                n++;
            }
            Log.Info($"took over where {n} windows were before Expanse moved them");
        }
        catch (Exception e) { Log.Info($"originals: couldn't take over: {e.Message}"); }
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
                if (e.Maximised && !IsZoomed((nint)e.Hwnd)) ShowWindow((nint)e.Hwnd, SW_SHOWMAXIMIZED);
                n++;
            }
            Log.Info($"restored {n} of {entries.Count} windows from the last run");
            System.IO.File.Delete(File);
        }
        catch (Exception e) { Log.Info($"originals: couldn't restore: {e.Message}"); }
    }
}
