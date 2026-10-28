using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// The icon in the taskbar's notification area, Expanse's menu-bar item on the Mac: click it to
/// zoom out, right-click for the menu. The glyph is the Mac's (three grouped rectangles), drawn
/// at the tray's size in black or white to suit the taskbar.
sealed class Tray : IDisposable
{
    public const uint WM_TRAY = WM_APP + 2;
    public readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    public sealed record Item(string Text, Action? Run, bool Checked = false, bool Enabled = true,
        IReadOnlyList<Item?>? Children = null);

    readonly nint hwnd;
    nint icon;
    bool added;
    string tip = "Expanse";

    public Tray(nint hwnd)
    {
        this.hwnd = hwnd;
        Add();
    }

    /// Again after Explorer restarts (TaskbarCreated), which drops every tray icon.
    public void Add()
    {
        if (icon != 0) DestroyIcon(icon);
        icon = Glyph(TaskbarIsLight());
        var data = Data(Shell32.NIF_MESSAGE | Shell32.NIF_ICON | Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
        added = Shell32.Shell_NotifyIcon(Shell32.NIM_ADD, ref data);
        data.uVersion = Shell32.NOTIFYICON_VERSION_4;
        Shell32.Shell_NotifyIcon(Shell32.NIM_SETVERSION, ref data);
        Log.Info(added ? "tray icon added" : "tray icon: Shell_NotifyIcon refused");
    }

    public void SetTip(string text)
    {
        tip = text;
        var data = Data(Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
        Shell32.Shell_NotifyIcon(Shell32.NIM_MODIFY, ref data);
    }

    /// A notification from the icon (shown as a toast), unless you're in Focus or Do Not Disturb.
    public void Notify(string title, string text)
    {
        var data = Data(Shell32.NIF_INFO);
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = Shell32.NIIF_INFO | Shell32.NIIF_RESPECT_QUIET_TIME;
        bool shown = Shell32.Shell_NotifyIcon(Shell32.NIM_MODIFY, ref data);
        Log.Info($"tray notification \"{title}\": {(shown ? "sent" : "refused")}");
    }

    /// The taskbar switched between light and dark.
    public void ThemeChanged()
    {
        nint old = icon;
        icon = Glyph(TaskbarIsLight());
        var data = Data(Shell32.NIF_ICON);
        Shell32.Shell_NotifyIcon(Shell32.NIM_MODIFY, ref data);
        if (old != 0) DestroyIcon(old);
    }

    /// Our WM_TRAY callback (NOTIFYICON_VERSION_4: the event in lParam's low word, the anchor
    /// point in wParam). Left click runs `click`; right click (or Shift+F10), or a click on a
    /// notification, shows `menu()`.
    public void Handle(nint wParam, nint lParam, Action click, Func<IReadOnlyList<Item?>> menu)
    {
        uint ev = (uint)(lParam & 0xFFFF);
        if (ev == Shell32.NIN_SELECT || ev == Shell32.NIN_KEYSELECT) click();
        else if (ev == WM_CONTEXTMENU) ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF), menu());
        else if (ev == NIN_BALLOONUSERCLICK && GetCursorPos(out var p)) ShowMenu(p.X, p.Y, menu());
    }

    const uint NIN_BALLOONUSERCLICK = 0x405;

    /// `null` items are separators; items with Children open a submenu.
    void ShowMenu(int x, int y, IReadOnlyList<Item?> items)
    {
        var commands = new List<Item>();
        nint Build(IReadOnlyList<Item?> list)
        {
            nint menu = CreatePopupMenu();
            foreach (var item in list)
            {
                if (item == null) { AppendMenu(menu, MF_SEPARATOR, 0, null); continue; }
                uint state = (item.Checked ? MF_CHECKED : 0) | (item.Enabled ? 0 : MF_GRAYED);
                if (item.Children != null)
                {
                    AppendMenu(menu, MF_POPUP | state, Build(item.Children), item.Text);
                    continue;
                }
                commands.Add(item);
                AppendMenu(menu, MF_STRING | state, commands.Count, item.Text);
            }
            return menu;
        }
        nint root = Build(items);
        // The menu only closes on a click elsewhere if its window is in the foreground.
        SetForegroundWindow(hwnd);
        uint picked = TrackPopupMenu(root, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, x, y, 0, hwnd, 0);
        PostMessage(hwnd, WM_NULL, 0, 0);
        DestroyMenu(root); // and its submenus
        if (picked > 0 && picked <= commands.Count)
        {
            var chosen = commands[(int)picked - 1];
            Log.Info($"tray menu: {chosen.Text}");
            chosen.Run?.Invoke();
        }
    }

    NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = icon,
        szTip = tip,
        szInfo = "",
        szInfoTitle = "",
    };

    static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
    }

    /// One large rounded rectangle and two small ones stacked beside it, outlined.
    static nint Glyph(bool lightTaskbar)
    {
        int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 16f;
            using var pen = new Pen(lightTaskbar ? Color.FromArgb(30, 30, 30) : Color.White, Math.Max(1f, 1.3f * s));
            Rounded(g, pen, new RectangleF(1.2f * s, 3f * s, 7.6f * s, 10f * s), 1.6f * s);
            Rounded(g, pen, new RectangleF(10.4f * s, 3f * s, 4.4f * s, 4.2f * s), 1.2f * s);
            Rounded(g, pen, new RectangleF(10.4f * s, 8.8f * s, 4.4f * s, 4.2f * s), 1.2f * s);
        }
        return bmp.GetHicon();
    }

    static void Rounded(Graphics g, Pen pen, RectangleF r, float radius)
    {
        float d = radius * 2;
        using var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }

    public void Dispose()
    {
        if (added)
        {
            var data = Data(0);
            Shell32.Shell_NotifyIcon(Shell32.NIM_DELETE, ref data);
            added = false;
        }
        if (icon != 0) { DestroyIcon(icon); icon = 0; }
    }
}
