using System.Diagnostics;
using System.Runtime.InteropServices;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// Admin windows: an admin terminal, Task Manager, an installer, regedit. Windows won't let a
/// program move the windows of one running at a higher integrity level (UIPI), so Expanse run
/// normally can't place them, and leaves them off the canvas, where they are. Run as
/// administrator ("Include Admin Windows" on the tray menu) it can.
static class Elevation
{
    const int Medium = 0x2000, High = 0x3000;

    /// Expanse's own integrity level, and whether it's administrator's (or above).
    public static readonly int Self = Of(Kernel32.GetCurrentProcessId());
    public static bool IsElevated => Self >= High;

    /// Whether an Expanse run as you (not administrator) couldn't move this window.
    public static bool AdminWindow(CanvasWindow w) => Of(w.Pid) > Medium;

    /// Whether `hwnd` belongs to a process Expanse can't move windows of.
    public static bool AboveUs(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return Of(pid) > Self;
    }

    /// A process's integrity level. One whose level can't be read is taken to be above ours: it's
    /// elevated or protected.
    public static int Of(uint pid)
    {
        nint process = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return int.MaxValue;
        try
        {
            if (!Advapi32.OpenProcessToken(process, Advapi32.TOKEN_QUERY, out nint token)) return int.MaxValue;
            try
            {
                Advapi32.GetTokenInformation(token, Advapi32.TokenIntegrityLevel, 0, 0, out int size);
                if (size <= 0) return int.MaxValue;
                nint buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!Advapi32.GetTokenInformation(token, Advapi32.TokenIntegrityLevel, buffer, size, out _)) return int.MaxValue;
                    nint sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
                    int count = Marshal.ReadByte(Advapi32.GetSidSubAuthorityCount(sid));
                    return Marshal.ReadInt32(Advapi32.GetSidSubAuthority(sid, (uint)(count - 1)));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { Kernel32.CloseHandle(token); }
        }
        finally { Kernel32.CloseHandle(process); }
    }

    /// Starts another Expanse as administrator, after Windows asks you. It waits for this one to
    /// hand over (see Program). Null if it started; otherwise why not.
    public static string? StartElevated()
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = Shell32.SEE_MASK_NOASYNC,
            lpVerb = "runas",
            lpFile = Environment.ProcessPath,
            nShow = SW_SHOWNORMAL,
        };
        if (Shell32.ShellExecuteEx(ref info)) return null;
        int error = Marshal.GetLastPInvokeError();
        return error == 1223 ? "you said no" : $"couldn't start (error {error})";
    }

    /// Starts another Expanse as you, not administrator: through Explorer, since anything this
    /// process starts itself would be elevated too. Null if it started; otherwise why not.
    public static string? StartNormal()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Environment.ProcessPath}\"") { UseShellExecute = false });
            return null;
        }
        catch (Exception e) { return $"couldn't start: {e.Message}"; }
    }
}
