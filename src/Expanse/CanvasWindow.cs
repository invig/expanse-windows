using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// A window on the canvas. `Frame` is its visible frame (no invisible resize borders) in canvas
/// space; `Insets` is how far the window rect reaches past that frame on each side.
sealed class CanvasWindow
{
    public readonly nint Hwnd;
    public readonly uint Pid;
    public readonly string Class;
    /// The exe's name ("msedge") and full path.
    public readonly string App, AppPath;
    /// What to call it: the exe's description ("Microsoft Edge"), as the Mac shows app names.
    public readonly string AppName;
    public string Title;
    public RectD Frame;
    public RECT Insets;
    public SIZE WindowSize;
    /// Where we last put the visible frame on screen; a window found elsewhere was moved by
    /// someone else (the user, the app), and its canvas spot follows.
    public POINT? Placed;
    /// Its window rect before Expanse first moved it, for putting it back on quit.
    public RECT? Original;
    /// The size you chose (by dragging an edge); on a smaller screen the window is shrunk to fit
    /// and grows back on a bigger one.
    public SizeD Preferred;
    /// Where it sits in the home layout: the arrangement at full chosen sizes (the big screen). On
    /// a smaller screen the canvas is derived from it, so it comes back exactly.
    public Vec? Home;
    /// The size Expanse last gave it, so our resizes aren't taken for yours.
    public SizeD? LastSized;

    public CanvasWindow(nint hwnd)
    {
        Hwnd = hwnd;
        GetWindowThreadProcessId(hwnd, out Pid);
        Class = ClassName(hwnd);
        string? path = Kernel32.ProcessPath(Pid);
        App = Path.GetFileNameWithoutExtension(path ?? "?");
        AppPath = path ?? "";
        Title = User32.Title(hwnd);
        AppName = DisplayName(path, App, Title);
    }

    static readonly Dictionary<string, string> displayNames = new(StringComparer.OrdinalIgnoreCase);

    static string DisplayName(string? path, string app, string title)
    {
        // Store apps all run in ApplicationFrameHost; their window title is the app's name.
        if (app.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return title;
        if (app.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return "File Explorer";
        if (path == null) return app;
        if (!displayNames.TryGetValue(path, out var name))
        {
            try { name = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); }
            catch (Exception) { name = null; }
            if (name != null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4]; // Notepad's says "Notepad.exe"
            displayNames[path] = name = string.IsNullOrEmpty(name) ? app : name;
        }
        return name;
    }

    /// The visible frame on screen now, and the insets from the window rect, from the system.
    public RECT ReadScreenFrame()
    {
        GetWindowRect(Hwnd, out var r);
        var f = Dwm.FrameBounds(Hwnd);
        Insets = new RECT(f.Left - r.Left, f.Top - r.Top, r.Right - f.Right, r.Bottom - f.Bottom);
        WindowSize = new SIZE { cx = r.Width, cy = r.Height };
        return f;
    }

    /// The part of the window a picture shows, in window-rect coordinates: the visible frame.
    public RECT SourceRect => new(Insets.Left, Insets.Top, WindowSize.cx - Insets.Right, WindowSize.cy - Insets.Bottom);

    public override string ToString() => $"{App}[{Hwnd:X}] \"{Title}\"";

    static readonly HashSet<string> shellClasses = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow",
    };

    /// Top-level windows that belong on the canvas, topmost first (EnumWindows order): the
    /// Alt-Tab set, minus minimised, cloaked (other virtual desktops, suspended UWP apps) and
    /// owned windows, and minus our own.
    public static List<nint> Scan()
    {
        var found = new List<nint>();
        uint self = Kernel32.GetCurrentProcessId();
        EnumWindows((h, _) =>
        {
            if (Belongs(h, self)) found.Add(h);
            return true;
        }, 0);
        return found;
    }

    static bool Belongs(nint h, uint self)
    {
        if (!IsWindowVisible(h) || IsIconic(h)) return false;
        long ex = ExStyle(h);
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
        if ((ex & WS_EX_NOACTIVATE) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if (GetWindow(h, GW_OWNER) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if ((Style(h) & WS_CHILD) != 0) return false;
        GetWindowThreadProcessId(h, out uint pid);
        if (pid == self) return false;
        if (Dwm.IsCloaked(h)) return false;
        if (shellClasses.Contains(ClassName(h))) return false;
        var f = Dwm.FrameBounds(h);
        if (f.Width < 40 || f.Height < 30) return false;
        return GetWindowTextLength(h) > 0;
    }
}
