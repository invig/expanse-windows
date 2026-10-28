using System.Runtime.InteropServices;
using System.Text;

namespace Expanse.Native;

// The slice of Win32 the projects use, declared by hand (classic DllImport: delegates and
// strings marshal without ceremony). Everything is in physical pixels: each exe is
// Per-Monitor-V2 DPI aware through src/app.manifest.

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public override readonly string ToString() => $"{Left},{Top} {Width}x{Height}";
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

[StructLayout(LayoutKind.Sequential)]
public struct SIZE { public int cx, cy; }

[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}

public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);
public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct WNDCLASSEX
{
    public uint cbSize;
    public uint style;
    public nint lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public string? lpszMenuName;
    public string lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
public struct MONITORINFO
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct PAINTSTRUCT
{
    public nint hdc;
    public int fErase;
    public RECT rcPaint;
    public int fRestore;
    public int fIncUpdate;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct WINDOWPLACEMENT
{
    public uint length;
    public uint flags;
    public uint showCmd;
    public POINT ptMinPosition;
    public POINT ptMaxPosition;
    public RECT rcNormalPosition;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct NOTIFYICONDATA
{
    public uint cbSize;
    public nint hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public nint hIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    public uint dwState;
    public uint dwStateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
    public uint uVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
    public uint dwInfoFlags;
    public Guid guidItem;
    public nint hBalloonIcon;
}

public delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);

[StructLayout(LayoutKind.Sequential)]
public struct INPUT
{
    public uint type;
    public InputUnion u;
}

[StructLayout(LayoutKind.Explicit)]
public struct InputUnion
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT
{
    public int dx, dy;
    public int mouseData;
    public uint dwFlags;
    public uint time;
    public nint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public nint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct DWM_THUMBNAIL_PROPERTIES
{
    public uint dwFlags;
    public RECT rcDestination;
    public RECT rcSource;
    public byte opacity;
    public int fVisible;
    public int fSourceClientAreaOnly;
}

// CharSet.Unicode throughout: DefWindowProc, GetMessage, DispatchMessage and friends must be the
// W versions for Unicode windows (the A ones truncate titles to their first letter).
public static class User32
{
    const string Dll = "user32.dll";

    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const long WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_CHILD = 0x40000000,
        WS_CAPTION = 0x00C00000, WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_MAXIMIZE = 0x01000000;
    public const long WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000,
        WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20,
        WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const uint GA_ROOTOWNER = 3;
    public const uint GW_OWNER = 4;

    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10,
        SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80, SWP_NOOWNERZORDER = 0x200, SWP_ASYNCWINDOWPOS = 0x4000;
    public static readonly nint HWND_TOP = 0, HWND_TOPMOST = -1, HWND_NOTOPMOST = -2, HWND_BOTTOM = 1;

    public const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_SHOWMINIMIZED = 2, SW_SHOWMAXIMIZED = 3,
        SW_SHOWNOACTIVATE = 4, SW_SHOW = 5, SW_MINIMIZE = 6, SW_SHOWNA = 8, SW_RESTORE = 9;

    public const uint WM_DESTROY = 0x2, WM_PAINT = 0xF, WM_CLOSE = 0x10, WM_QUIT = 0x12, WM_ERASEBKGND = 0x14,
        WM_SETCURSOR = 0x20, WM_MOUSEACTIVATE = 0x21, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_TIMER = 0x113,
        WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204,
        WM_MOUSEWHEEL = 0x20A, WM_HOTKEY = 0x312, WM_DISPLAYCHANGE = 0x7E, WM_DPICHANGED = 0x2E0,
        WM_NCHITTEST = 0x84, WM_ACTIVATE = 0x6, WM_SYSCOMMAND = 0x112, WM_APP = 0x8000;
    public const int SC_KEYMENU = 0xF100;
    public const int MA_ACTIVATE = 1, MA_NOACTIVATE = 3;

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    public const uint MAPVK_VSC_TO_VK = 1;
    public const int VK_ESCAPE = 0x1B, VK_MENU = 0x12, VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_LWIN = 0x5B,
        VK_RETURN = 0x0D, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;

    public const uint PM_REMOVE = 1;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
    public const int IDC_ARROW = 32512, IDC_HAND = 32649;
    public const uint CS_HREDRAW = 2, CS_VREDRAW = 1;
    public const uint SPI_GETDESKWALLPAPER = 0x73;
    public const int SM_CXSCREEN = 0, SM_CYSCREEN = 1, SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77,
        SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    public const uint LWA_ALPHA = 2, LWA_COLORKEY = 1;

    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8, KEYEVENTF_EXTENDEDKEY = 0x1;
    public const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4,
        MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;

    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool IsWindow(nint hwnd);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool IsIconic(nint hwnd);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool IsZoomed(nint hwnd);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool IsHungAppWindow(nint hwnd);
    [DllImport(Dll, EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport(Dll, EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(nint hwnd);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hwnd, StringBuilder sb, int max);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int GetClassName(nint hwnd, StringBuilder sb, int max);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool GetClientRect(nint hwnd, out RECT r);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint GetLastActivePopup(nint hwnd);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool GetWindowPlacement(nint hwnd, ref WINDOWPLACEMENT wp);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool SetWindowPlacement(nint hwnd, ref WINDOWPLACEMENT wp);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint GetForegroundWindow();
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool BringWindowToTop(nint hwnd);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint SetActiveWindow(nint hwnd);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint SetFocus(nint hwnd);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool LockSetForegroundWindow(uint code);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(uint exStyle, string className, string? title, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool DestroyWindow(nint hwnd);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool PeekMessage(out MSG msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint DispatchMessage(ref MSG msg);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool PostThreadMessage(uint thread, uint msg, nint wParam, nint lParam);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern void PostQuitMessage(int code);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint SetTimer(nint hwnd, nint id, uint ms, nint proc);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool KillTimer(nint hwnd, nint id);

    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern uint MapVirtualKey(uint code, uint type);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern short GetAsyncKeyState(int vk);
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint MonitorFromPoint(POINT p, uint flags);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO mi);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int GetSystemMetrics(int index);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool SetProcessDpiAwarenessContext(nint ctx);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool GetCursorPos(out POINT p);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool SetCursorPos(int x, int y);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint LoadCursor(nint instance, nint name);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint SetCursor(nint cursor);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint action, uint param, StringBuilder sb, uint winIni);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool UpdateWindow(nint hwnd);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint BeginPaint(nint hwnd, out PAINTSTRUCT ps);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool EndPaint(nint hwnd, ref PAINTSTRUCT ps);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint GetDC(nint hwnd);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int FillRect(nint hdc, ref RECT r, nint brush);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int FrameRect(nint hdc, ref RECT r, nint brush);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int DrawText(nint hdc, string text, int len, ref RECT r, uint format);

    public const uint DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20, DT_CALCRECT = 0x400,
        DT_END_ELLIPSIS = 0x8000, DT_NOPREFIX = 0x800;
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern uint GetDpiForWindow(nint hwnd);

    // WinEvents
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MOVESIZESTART = 0x000A, EVENT_SYSTEM_MOVESIZEEND = 0x000B,
        EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017,
        EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003,
        EVENT_OBJECT_LOCATIONCHANGE = 0x800B, EVENT_OBJECT_NAMECHANGE = 0x800C,
        EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;
    public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    public const int OBJID_WINDOW = 0, CHILDID_SELF = 0;
    public const uint GA_ROOT = 2;
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool UnhookWinEvent(nint hook);

    // Menus
    public const uint MF_STRING = 0, MF_GRAYED = 1, MF_CHECKED = 8, MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;
    public const uint WM_NULL = 0, WM_CONTEXTMENU = 0x7B, WM_SETTINGCHANGE = 0x1A, WM_USER = 0x400;
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint CreatePopupMenu();
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool AppendMenu(nint menu, uint flags, nint id, string? text);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool DestroyMenu(nint menu);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool DestroyIcon(nint icon);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern uint GetDpiForSystem();
    public const int SM_CXSMICON = 49;

    public static long Style(nint hwnd) => GetWindowLongPtr(hwnd, GWL_STYLE);
    public static long ExStyle(nint hwnd) => GetWindowLongPtr(hwnd, GWL_EXSTYLE);

    public static string Title(nint hwnd)
    {
        int n = GetWindowTextLength(hwnd);
        if (n <= 0) return "";
        var sb = new StringBuilder(n + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(nint hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static (RECT monitor, RECT work) PrimaryMonitor()
    {
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY), ref mi);
        return (mi.rcMonitor, mi.rcWork);
    }

    public static nint RegisterWindowClass(string name, WndProc proc, nint cursor = 0)
    {
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = Kernel32.GetModuleHandle(null),
            hCursor = cursor != 0 ? cursor : LoadCursor(0, IDC_ARROW),
            lpszClassName = name,
        };
        RegisterClassEx(ref wc);
        return wc.hInstance;
    }
}

public static class Gdi32
{
    const string Dll = "gdi32.dll";
    public const int TRANSPARENT = 1;
    public const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    public const int HALFTONE = 4;

    [DllImport(Dll)] public static extern nint CreateCompatibleDC(nint hdc);
    [DllImport(Dll)] public static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [DllImport(Dll)] public static extern nint SelectObject(nint hdc, nint obj);
    [DllImport(Dll)] public static extern bool DeleteObject(nint obj);
    [DllImport(Dll)] public static extern bool DeleteDC(nint hdc);
    [DllImport(Dll)] public static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport(Dll)] public static extern bool StretchBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport(Dll)] public static extern int SetStretchBltMode(nint hdc, int mode);
    [DllImport(Dll)] public static extern nint CreateSolidBrush(uint colorref);
    [DllImport(Dll)] public static extern nint CreatePen(int style, int width, uint colorref);
    [DllImport(Dll)] public static extern nint GetStockObject(int obj);
    [DllImport(Dll)] public static extern int SetBkMode(nint hdc, int mode);
    [DllImport(Dll)] public static extern uint SetTextColor(nint hdc, uint colorref);
    [DllImport(Dll)] public static extern bool RoundRect(nint hdc, int l, int t, int r, int b, int w, int h);
    [DllImport(Dll)] public static extern bool Rectangle(nint hdc, int l, int t, int r, int b);
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern nint CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline,
        uint strike, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string face);

    public const int NULL_BRUSH = 5, NULL_PEN = 8;
    public static uint RGB(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));
}

public static class Dwm
{
    const string Dll = "dwmapi.dll";
    public const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_RECTSOURCE = 0x2, DWM_TNP_OPACITY = 0x4,
        DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14, DWMWA_EXCLUDED_FROM_PEEK = 12,
        DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    [DllImport(Dll)] public static extern int DwmRegisterThumbnail(nint dest, nint src, out nint thumb);
    [DllImport(Dll)] public static extern int DwmUnregisterThumbnail(nint thumb);
    [DllImport(Dll)] public static extern int DwmUpdateThumbnailProperties(nint thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport(Dll)] public static extern int DwmQueryThumbnailSourceSize(nint thumb, out SIZE size);
    [DllImport(Dll)] public static extern int DwmGetWindowAttribute(nint hwnd, int attr, out RECT value, int size);
    [DllImport(Dll)] public static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
    [DllImport(Dll)] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [DllImport(Dll)] public static extern int DwmFlush();

    public static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int c, sizeof(int)) == 0 && c != 0;

    /// The visible frame: the window rect minus Windows 10/11's invisible resize borders.
    public static RECT FrameBounds(nint hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0) return r;
        User32.GetWindowRect(hwnd, out r);
        return r;
    }
}

public static class Shell32
{
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;
    public const uint NOTIFYICON_VERSION_4 = 4, NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401;
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
}

public static class Kernel32
{
    const string Dll = "kernel32.dll";
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
    [DllImport(Dll)] public static extern uint GetCurrentThreadId();
    [DllImport(Dll)] public static extern uint GetCurrentProcessId();
    [DllImport(Dll, SetLastError = true)] public static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport(Dll)] public static extern bool CloseHandle(nint h);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder sb, ref int size);
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public static string? ProcessPath(uint pid)
    {
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }
}
