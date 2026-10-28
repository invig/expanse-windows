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
    public readonly string App;
    public string Title;
    public RectD Frame;
    public RECT Insets;
    public SIZE WindowSize;
    /// Where we last put the visible frame on screen; a window found elsewhere was moved by
    /// someone else (the user, the app), and its canvas spot follows.
    public POINT? Placed;
    /// Its window rect before Expanse first moved it, for putting it back on quit.
    public RECT? Original;

    public CanvasWindow(nint hwnd)
    {
        Hwnd = hwnd;
        GetWindowThreadProcessId(hwnd, out Pid);
        Class = ClassName(hwnd);
        App = Path.GetFileNameWithoutExtension(Kernel32.ProcessPath(Pid) ?? "?");
        Title = User32.Title(hwnd);
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
