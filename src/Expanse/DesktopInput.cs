using System.Runtime.InteropServices;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// The empty desktop, as on the Mac (where Expanse's backdrop is the desktop): a click on it zooms
/// out, and a scroll or pinch over it starts moving around the canvas from 100%.
///
/// A low-level mouse hook sees the clicks and wheel turns. It runs on a thread of its own, so the
/// pointer never waits on Expanse's UI thread (Windows holds every mouse event until a hook
/// returns), and only posts the canvas a message. Wheel turns over the desktop are kept from it
/// (Ctrl+wheel would resize its icons); clicks go through as usual.
sealed class DesktopInput : IDisposable
{
    public const uint WM_DESKTOP_CLICK = WM_APP + 4, WM_DESKTOP_WHEEL = WM_APP + 5;
    /// Packed into a WM_DESKTOP_WHEEL's wParam with the delta (high word).
    public const int Horizontal = 1, Ctrl = 2, Shift = 4;

    readonly nint overlay;
    readonly HookProc proc;
    nint hook;
    uint threadId;
    POINT downAt;
    bool downOnDesktop;

    /// Set by the canvas: at 100%, running, so the desktop's input is Expanse's to use.
    public volatile bool Armed;

    /// When the last button went down, and the last mouse event of any kind, in the input's own
    /// time (GetTickCount, as GetLastInputInfo): for telling clicks and key presses from the pointer
    /// moving (see Canvas.InputSince).
    public uint LastButtonDown => (uint)Interlocked.CompareExchange(ref lastButtonDown, 0, 0);
    public uint LastMouseEvent => (uint)Interlocked.CompareExchange(ref lastMouseEvent, 0, 0);
    int lastButtonDown, lastMouseEvent;

    delegate nint HookProc(int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public nint extra; }

    public DesktopInput(nint overlay)
    {
        this.overlay = overlay;
        proc = Hook;
        var thread = new Thread(Run) { IsBackground = true, Name = "desktop input" };
        thread.Start();
    }

    void Run()
    {
        threadId = Kernel32.GetCurrentThreadId();
        hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, proc, Kernel32.GetModuleHandle(null), 0);
        Log.Info(hook != 0 ? "desktop input: watching" : $"desktop input: no hook ({Marshal.GetLastPInvokeError()})");
        while (GetMessage(out var m, 0, 0, 0) > 0) DispatchMessage(ref m);
        if (hook != 0) UnhookWindowsHookEx(hook);
    }

    nint Hook(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            Interlocked.Exchange(ref lastMouseEvent, (int)m.time);
            if ((uint)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN) Interlocked.Exchange(ref lastButtonDown, (int)m.time);
        }
        if (code >= 0 && Armed)
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            switch ((uint)wParam)
            {
                case WM_LBUTTONDOWN:
                    downAt = m.pt;
                    downOnDesktop = IsDesktop(m.pt);
                    break;
                case WM_LBUTTONUP:
                    if (downOnDesktop && Math.Abs(m.pt.X - downAt.X) < 4 && Math.Abs(m.pt.Y - downAt.Y) < 4 && IsDesktop(m.pt))
                        PostMessage(overlay, WM_DESKTOP_CLICK, 0, Pack(m.pt));
                    downOnDesktop = false;
                    break;
                case WM_MOUSEWHEEL:
                case WM_MOUSEHWHEEL:
                    if (!IsDesktop(m.pt)) break;
                    int flags = ((uint)wParam == WM_MOUSEHWHEEL ? Horizontal : 0) |
                                (GetAsyncKeyState(VK_CONTROL) < 0 ? Ctrl : 0) | (GetAsyncKeyState(VK_SHIFT) < 0 ? Shift : 0);
                    PostMessage(overlay, WM_DESKTOP_WHEEL, (nint)(((m.mouseData >> 16) << 16) | (uint)flags), Pack(m.pt));
                    return 1; // the desktop doesn't get it
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    static nint Pack(POINT p) => (nint)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF));

    /// The desktop itself (not a window over it): its shell windows, or the icons on them.
    static bool IsDesktop(POINT p)
    {
        nint root = GetAncestor(WindowFromPoint(p), GA_ROOT);
        return root != 0 && ClassName(root) is "Progman" or "WorkerW";
    }

    /// After a click on the desktop: true if it left no icon selected (a click on empty desktop
    /// clears the selection; one on an icon selects it).
    public static bool NothingSelected(POINT p)
    {
        nint h = WindowFromPoint(p);
        nint list = ClassName(h) == "SysListView32" ? h : 0;
        if (list == 0) return true; // icons hidden: it's all empty desktop
        return SendMessage(list, 0x1032 /* LVM_GETSELECTEDCOUNT */, 0, 0) == 0;
    }

    public void Dispose()
    {
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, 0, 0);
    }
}
