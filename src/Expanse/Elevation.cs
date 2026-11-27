using System.Diagnostics;
using System.Runtime.InteropServices;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// Admin windows: an admin terminal, Task Manager, an installer, regedit. Windows won't let a
/// program move the windows of one running at a higher integrity level (UIPI), so Expanse run
/// normally can't place them, and leaves them off the canvas, where they are. Run as
/// administrator ("Include admin windows" in Settings) it can.
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

    public const string Declined = "you said no";

    /// Starts another Expanse as administrator, after Windows asks you. It waits for this one to
    /// hand over (see Program). Null if it started; otherwise why not.
    public static string? StartElevated()
    {
        if (AdminOnlyPath(out string why) is not string path) return why;
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = Shell32.SEE_MASK_NOASYNC,
            lpVerb = "runas",
            lpFile = path,
            lpDirectory = Parent(path),
            nShow = SW_SHOWNORMAL,
        };
        if (Shell32.ShellExecuteEx(ref info)) return null;
        int error = Marshal.GetLastPInvokeError();
        return error == 1223 ? Declined : $"couldn't start (error {error})";
    }

    /// Starts another Expanse as you, not administrator: through Explorer, since anything this
    /// process starts itself would be elevated too. Null if it started; otherwise why not.
    public static string? StartNormal()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Explorer, $"\"{Environment.ProcessPath}\"") { UseShellExecute = false });
            return null;
        }
        catch (Exception e) { return $"couldn't start: {e.Message}"; }
    }

    /// Explorer by its full path: a bare name is looked for in the current folder too, which run as
    /// administrator could be one anything run as you can write to.
    public static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    // MARK: Only administrators' Expanse.exe

    // What would let a program run as you put something else where Windows starts Expanse as
    // administrator. A file: writing it, deleting or renaming it, or changing who may. Its folder:
    // adding a file or folder (a DLL Windows would load), deleting or renaming what's in it, or
    // itself. A folder above: deleting or renaming what's in it, or itself.
    const uint Delete = 0x10000, WriteDac = 0x40000, WriteOwner = 0x80000;
    const uint FileChanges = 0x2 | 0x4 | 0x10 | Delete | WriteDac | WriteOwner;
    const uint FolderChanges = 0x2 | 0x4 | 0x40 | Delete | WriteDac | WriteOwner;
    const uint AboveChanges = 0x40 | Delete | WriteDac | WriteOwner;

    /// SYSTEM, Administrators, TrustedInstaller. Anything you own you could have changed before
    /// taking your own rights away, so nothing in the path may be yours.
    static readonly HashSet<string> adminOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
    };

    /// This Expanse.exe, by the path Windows is to start as administrator, if only administrators
    /// could have put it there and nothing run as you can change what's at that path: it, its
    /// folder, what's beside it, and every folder above, as in Program Files. Windows starts what's
    /// at the path when you say yes, so a copy you can change (somewhere in your user folder) would
    /// be whatever another program put there by then. Null otherwise, with why not.
    static string? AdminOnlyPath(out string why)
    {
        nint token = 0;
        try
        {
            if (!Advapi32.OpenProcessToken(-1, Advapi32.TOKEN_QUERY | Advapi32.TOKEN_DUPLICATE, out nint process))
                throw new System.ComponentModel.Win32Exception();
            try
            {
                if (!Advapi32.DuplicateToken(process, Advapi32.SecurityImpersonation, out token))
                    throw new System.ComponentModel.Win32Exception();
            }
            finally { Kernel32.CloseHandle(process); }

            string dos, nt;
            using (var exe = Open(Environment.ProcessPath ?? throw new InvalidOperationException("no path to this Expanse"), follow: true))
                (dos, nt) = (FinalPath(exe, Kernel32.VOLUME_NAME_DOS), FinalPath(exe, Kernel32.VOLUME_NAME_NT));
            // \\?\C:\…: on a drive of this PC, not a network share.
            if (dos.Length < 8 || !dos.StartsWith(@"\\?\", StringComparison.Ordinal) || !char.IsAsciiLetter(dos[4]) || dos[5] != ':' || dos[6] != '\\')
            {
                why = $"Expanse.exe isn't on a drive of this PC ({dos})";
                return null;
            }
            string path = dos[4..], inVolume = path[3..];
            if (!nt.EndsWith(inVolume, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{dos} and {nt} aren't the same file");
            string volume = nt[..^inVolume.Length];
            string NtOf(string p) => volume + p[3..];

            string folder = Parent(path)!;
            string? problem = Changeable(path, NtOf(path), token, above: false);
            problem ??= Changeable(folder, NtOf(folder), token, above: false);
            // Nothing can be added beside it (checked just now), so what's there stays what's checked.
            if (problem == null)
                foreach (string name in Names(folder))
                {
                    string beside = folder.TrimEnd('\\') + '\\' + name;
                    if ((problem = Changeable(beside, NtOf(beside), token, above: false)) != null) break;
                }
            for (string? up = Parent(folder); problem == null && up != null; up = Parent(up))
                problem = Changeable(up, NtOf(up), token, above: true);
            if (problem != null)
            {
                why = $"{problem}, so Windows could start something else as administrator: " +
                      "put Expanse in a folder only administrators can change, such as Program Files, to include admin windows";
                return null;
            }
            why = "";
            return path;
        }
        catch (Exception e)
        {
            why = $"couldn't tell whether Expanse.exe is out of reach of programs run as you ({e.Message})";
            return null;
        }
        finally { if (token != 0) Kernel32.CloseHandle(token); }
    }

    /// Why a program run as you could change what's at `path`, or null if it couldn't. `nt` is
    /// where it should be (its device and path), as found from this Expanse.exe.
    static string? Changeable(string path, string nt, nint token, bool above)
    {
        using var handle = Open(path);
        if (!FinalPath(handle, Kernel32.VOLUME_NAME_NT).TrimEnd('\\').Equals(nt.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return $"{path} isn't where Expanse.exe was found";
        if (!Kernel32.GetFileInformationByHandle(handle, out var info)) throw new System.ComponentModel.Win32Exception();
        if ((info.dwFileAttributes & Kernel32.FILE_ATTRIBUTE_REPARSE_POINT) != 0) return $"{path} is a link";
        bool isFolder = (info.dwFileAttributes & Kernel32.FILE_ATTRIBUTE_DIRECTORY) != 0;
        uint changes = !isFolder ? FileChanges : above ? AboveChanges : FolderChanges;

        uint error = Advapi32.GetSecurityInfo(handle, Advapi32.SE_FILE_OBJECT,
            Advapi32.OWNER_SECURITY_INFORMATION | Advapi32.GROUP_SECURITY_INFORMATION | Advapi32.DACL_SECURITY_INFORMATION | Advapi32.LABEL_SECURITY_INFORMATION,
            out nint owner, out _, out _, out _, out nint descriptor);
        if (error != 0) throw new System.ComponentModel.Win32Exception((int)error);
        try
        {
            if (owner == 0 || !adminOwners.Contains(Sid(owner)))
                return $"{path} isn't administrators'";
            // FILE_GENERIC_READ, _WRITE, _EXECUTE, FILE_ALL_ACCESS.
            var mapping = new Advapi32.GENERIC_MAPPING { GenericRead = 0x120089, GenericWrite = 0x120116, GenericExecute = 0x1200A0, GenericAll = 0x1F01FF };
            var privileges = new byte[1024];
            int length = privileges.Length;
            if (!Advapi32.AccessCheck(descriptor, token, Advapi32.MAXIMUM_ALLOWED, ref mapping, privileges, ref length, out uint granted, out _))
                throw new System.ComponentModel.Win32Exception();
            return (granted & changes) != 0 ? $"you can change {path}" : null;
        }
        finally { Kernel32.LocalFree(descriptor); }
    }

    /// A file or folder to ask about: itself, or with `follow`, where a link leads.
    static Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path, bool follow = false)
    {
        var handle = Kernel32.CreateFile(@"\\?\" + path, Kernel32.READ_CONTROL | Kernel32.FILE_READ_ATTRIBUTES, Kernel32.FILE_SHARE_ALL, 0,
            Kernel32.OPEN_EXISTING, Kernel32.FILE_FLAG_BACKUP_SEMANTICS | (follow ? 0 : Kernel32.FILE_FLAG_OPEN_REPARSE_POINT), 0);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), $"couldn't open {path}");
        return handle;
    }

    /// The folder `path` (C:\…) is in; null for a drive's own folder.
    static string? Parent(string path)
    {
        if (path.Length <= 3) return null;
        int slash = path.LastIndexOf('\\');
        return slash <= 2 ? path[..3] : path[..slash];
    }

    /// What's in `folder`, by name.
    static List<string> Names(string folder)
    {
        var names = new List<string>();
        nint find = Kernel32.FindFirstFile(@"\\?\" + folder.TrimEnd('\\') + @"\*", out var data);
        if (find == -1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), $"couldn't list {folder}");
        try
        {
            do
                if (data.cFileName is not ("." or "..")) names.Add(data.cFileName);
            while (Kernel32.FindNextFile(find, out data));
            int error = Marshal.GetLastPInvokeError();
            if (error != 18) throw new System.ComponentModel.Win32Exception(error, $"couldn't list {folder}"); // ERROR_NO_MORE_FILES
        }
        finally { Kernel32.FindClose(find); }
        return names;
    }

    static string Sid(nint sid)
    {
        if (!Advapi32.ConvertSidToStringSid(sid, out nint text)) throw new System.ComponentModel.Win32Exception();
        try { return Marshal.PtrToStringUni(text) ?? ""; }
        finally { Kernel32.LocalFree(text); }
    }

    static string FinalPath(SafeHandle handle, uint volume)
    {
        var buffer = new char[1024];
        for (int tries = 0; tries < 2; tries++)
        {
            uint n = Kernel32.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, volume);
            if (n == 0) throw new System.ComponentModel.Win32Exception();
            if (n < buffer.Length) return new string(buffer, 0, (int)n);
            buffer = new char[n + 1];
        }
        throw new InvalidOperationException("the path keeps getting longer");
    }
}
