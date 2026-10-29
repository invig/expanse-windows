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
    /// How far the last fit pushed it to clear an overlap (see Canvas.FitLayoutToScreen).
    public Vec FitPush;
    /// The size Expanse last gave it, so our resizes aren't taken for yours.
    public SizeD? LastSized;
    /// When Expanse last resized it (Environment.TickCount64): an app catching up with that isn't
    /// you choosing a size.
    public long LastSizedAt;

    /// You maximised it. Windows holds a maximised window to the screen (moving it does nothing),
    /// so on the canvas it's a window the size of the work area: maximised while the camera has
    /// it where maximised windows sit, and restored to a window of the same size (RestoredByUs)
    /// to be moved anywhere else. NormalRect is the size and place you'd restore it to yourself.
    public bool Maximised, RestoredByUs;
    public RECT? NormalRect;

    /// Restores a maximised window to an ordinary one of the same size in the same place, so it
    /// can be moved. `work` is the work area (WINDOWPLACEMENT is relative to it).
    public void RestoreForMoving(RECT work)
    {
        var wp = Placement();
        NormalRect ??= wp.rcNormalPosition;
        GetWindowRect(Hwnd, out var r);
        var f = Dwm.FrameBounds(Hwnd);
        wp.showCmd = SW_SHOWNOACTIVATE;
        wp.rcNormalPosition = new RECT(r.Left - work.Left, r.Top - work.Top, r.Right - work.Left, r.Bottom - work.Top);
        SetWindowPlacement(Hwnd, ref wp);
        RestoredByUs = true;
        // An ordinary window's borders differ from a maximised one's: its visible frame is made
        // the maximised one's exactly.
        ReadScreenFrame();
        SetWindowPos(Hwnd, 0, f.Left - Insets.Left, f.Top - Insets.Top, f.Width + Insets.Left + Insets.Right,
            f.Height + Insets.Top + Insets.Bottom, SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE);
        ReadScreenFrame();
    }

    /// Maximised again, restoring (when you un-maximise it) to the size and place you had.
    public void Maximise()
    {
        var wp = Placement();
        wp.showCmd = SW_SHOWMAXIMIZED;
        if (NormalRect is RECT n) wp.rcNormalPosition = n;
        SetWindowPlacement(Hwnd, ref wp);
        RestoredByUs = false;
        ReadScreenFrame();
    }

    WINDOWPLACEMENT Placement()
    {
        var wp = new WINDOWPLACEMENT { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(Hwnd, ref wp);
        wp.flags = 0;
        return wp;
    }

    /// Apps whose window shows a display somewhere else (a VM, a remote PC): they size it to
    /// match, and resizing it changes that display's resolution. Expanse leaves their size alone
    /// and takes whatever size they choose (the Mac's sizesOwnWindows).
    static readonly HashSet<string> sizesOwnWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "mstsc", "msrdc", "Windows365", "vmconnect", "VirtualBoxVM", "vmware", "vmplayer", "parsecd", "Moonlight",
        "vncviewer", "tvnviewer", "rustdesk", "AnyDesk", "TeamViewer", "qemu-system-x86_64", "qemu-system-aarch64",
    };
    public bool SizesItself => sizesOwnWindows.Contains(App);

    /// A dialog or other owned window (Find, Properties, "Save changes?") belongs to a canvas
    /// window, its Owner, and isn't on the canvas by itself. Windows doesn't move owned windows
    /// with their owner, so Expanse does: its frame is kept relative to the owner's, and goes
    /// wherever the owner goes.
    public CanvasWindow? Owner;
    /// This window's owned windows, bottom-most first.
    public readonly List<CanvasWindow> Owned = new();
    RectD frame;
    Vec ownerOffset;

    public RectD Frame
    {
        get => Owner is CanvasWindow o ? new RectD(o.Frame.X + ownerOffset.X, o.Frame.Y + ownerOffset.Y, frame.W, frame.H) : frame;
        set
        {
            if (Owner is CanvasWindow o) ownerOffset = new Vec(value.X - o.Frame.X, value.Y - o.Frame.Y);
            frame = value;
        }
    }

    /// The window and its owned windows, bottom-most first: the order their pictures stack in.
    public IEnumerable<CanvasWindow> WithOwned() => Owned.Prepend(this);

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

    /// For the log: the app and the handle, never the title (titles can be private).
    public override string ToString() => $"{App}[{Hwnd:X}]";

    /// Any window, for the log: its app and handle.
    public static string Describe(nint hwnd)
    {
        if (hwnd == 0) return "nothing";
        GetWindowThreadProcessId(hwnd, out uint pid);
        return $"{Path.GetFileNameWithoutExtension(Kernel32.ProcessPath(pid) ?? "?")}[{hwnd:X}]";
    }

    static readonly HashSet<string> shellClasses = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow",
    };

    /// Top-level windows that belong on the canvas, topmost first (EnumWindows order): the
    /// Alt-Tab set, minus minimised, cloaked (other virtual desktops, suspended UWP apps) and
    /// owned windows, and minus our own. Admin windows Expanse can't move (see Elevation) are
    /// left off, and listed in `admin`.
    public static List<nint> Scan(List<nint>? admin = null)
    {
        var found = new List<nint>();
        uint self = Kernel32.GetCurrentProcessId();
        EnumWindows((h, _) =>
        {
            if (Belongs(h, self, admin)) found.Add(h);
            return true;
        }, 0);
        return found;
    }

    /// Owned windows with a title bar (dialogs, palettes), topmost first, each with the canvas
    /// window it belongs to (its root owner). Tooltips, menus and drop-downs have no title bar and
    /// don't count; nor do owned windows with their own taskbar button, which Scan takes.
    public static List<(nint hwnd, nint owner)> ScanOwned()
    {
        var found = new List<(nint, nint)>();
        uint self = Kernel32.GetCurrentProcessId();
        EnumWindows((h, _) =>
        {
            if (IsOwned(h, self)) found.Add((h, RootOwner(h)));
            return true;
        }, 0);
        return found;
    }

    /// The end of `h`'s chain of owners (`h` itself if it has none). GetAncestor's GA_ROOTOWNER
    /// only follows owners of popups, not of ordinary owned windows (a WinForms dialog, say).
    public static nint RootOwner(nint h)
    {
        for (int i = 0; i < 32 && GetWindow(h, GW_OWNER) is var o && o != 0; i++) h = o;
        return h;
    }

    static bool IsOwned(nint h, uint self)
    {
        if (GetWindow(h, GW_OWNER) == 0 || !IsWindowVisible(h) || IsIconic(h)) return false;
        long style = Style(h), ex = ExStyle(h);
        if ((style & WS_CHILD) != 0 || (ex & WS_EX_APPWINDOW) != 0) return false;
        if ((style & WS_CAPTION) != WS_CAPTION && ClassName(h) != "#32770") return false;
        GetWindowThreadProcessId(h, out uint pid);
        if (pid == self || Dwm.IsCloaked(h)) return false;
        var f = Dwm.FrameBounds(h);
        return f.Width >= 40 && f.Height >= 30 && (Elevation.IsElevated || !Elevation.AboveUs(h));
    }

    /// Every visible top-level window, topmost first, with what decides whether it's on the
    /// canvas (`ctl windows`, for diagnosing).
    public static string Describe()
    {
        var lines = new List<string>();
        uint self = Kernel32.GetCurrentProcessId();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            nint owner = GetWindow(h, GW_OWNER);
            var f = Dwm.FrameBounds(h);
            string kind = Belongs(h, self, null) ? "canvas" : IsOwned(h, self) ? "owned" : "-";
            if (Elevation.AboveUs(h)) kind += " (admin)";
            lines.Add($"0x{h:X} {kind,-6} {ClassName(h)} \"{User32.Title(h)}\" style 0x{Style(h):X} ex 0x{ExStyle(h):X} " +
                      $"owner 0x{owner:X} root 0x{RootOwner(h):X} {f.Left},{f.Top} {f.Width}x{f.Height}");
            return true;
        }, 0);
        return string.Join("\n", lines);
    }

    static bool Belongs(nint h, uint self, List<nint>? admin)
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
        if (GetWindowTextLength(h) == 0) return false;
        if (!Elevation.IsElevated && Elevation.AboveUs(h))
        {
            admin?.Add(h);
            return false;
        }
        return true;
    }
}
