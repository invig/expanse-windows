using System.Runtime.InteropServices;
using Microsoft.Win32;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// Windows' virtual desktops, for the Desktops panel beside the minimap (the Mac's Spaces panel).
/// Windows has no public way to list or switch them, so the list and the current one are read
/// from where Explorer keeps them (the registry), and switching is the shortcut you'd press:
/// Win+Ctrl+arrows, once per desktop along the way. Each desktop's windows are on the canvas while
/// it's the current one; the others' are cloaked, so they leave it and come back to their spots.
static class VirtualDesktops
{
    public readonly record struct Desktop(Guid Id, string Name);

    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    /// The desktops in order, and which is current (-1 if unknown).
    public static (List<Desktop> desktops, int current) Read()
    {
        var list = new List<Desktop>();
        int current = -1;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            if (key?.GetValue("VirtualDesktopIDs") is not byte[] ids) return (list, current);
            byte[]? now = key.GetValue("CurrentVirtualDesktop") as byte[];
            for (int i = 0; i + 16 <= ids.Length; i += 16)
            {
                var id = new Guid(ids.AsSpan(i, 16));
                using var desk = Registry.CurrentUser.OpenSubKey($@"{Key}\Desktops\{id:B}");
                string name = desk?.GetValue("Name") as string is { Length: > 0 } n ? n : $"Desktop {list.Count + 1}";
                if (now != null && now.Length == 16 && new Guid(now) == id) current = list.Count;
                list.Add(new Desktop(id, name));
            }
        }
        catch (Exception e) { Log.Info($"desktops: couldn't read ({e.Message})"); }
        return (list, current);
    }

    /// Goes `steps` desktops right (negative: left), as Win+Ctrl+Right/Left would.
    public static void Move(int steps)
    {
        if (steps == 0) return;
        ushort arrow = (ushort)(steps > 0 ? VK_RIGHT : VK_LEFT);
        var inputs = new List<INPUT>();
        void Key(ushort vk, bool up, bool extended = false)
        {
            var i = new INPUT { type = INPUT_KEYBOARD };
            i.u.ki.wVk = vk;
            i.u.ki.dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? 1u /* KEYEVENTF_EXTENDEDKEY */ : 0);
            inputs.Add(i);
        }
        Key(VK_LWIN, false, true);
        Key(VK_CONTROL, false);
        for (int n = 0; n < Math.Abs(steps); n++)
        {
            Key(arrow, false, true);
            Key(arrow, true, true);
        }
        Key(VK_CONTROL, true);
        Key(VK_LWIN, true, true);
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }
}
