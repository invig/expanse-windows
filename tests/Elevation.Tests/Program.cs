using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Expanse.Native;

// Fails while Expanse relaunches its own path with the runas verb from a folder the
// current user can write. Run (linux-x64 so the product assembly loads on this machine):
//   dotnet run --project tests/Elevation.Tests -p:RuntimeIdentifier=linux-x64
//
// The product calls ShellExecuteEx. The native stand-in in native/shell_stub.c is the
// Wine 10.0 shell32 behaviour of that call: runas launches the bytes at lpFile with
// the admin token. It swaps a writable lpFile at the start of the call, which is the
// moment after any check in StartElevated and before CreateProcess opens the path.

const string MarkerText = "replaced-before-elevation\n";

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("this host runs the stand-in on Linux");
    return 2;
}

string processPath = Environment.ProcessPath ?? "";
if (!processPath.EndsWith($"{Path.DirectorySeparatorChar}Expanse.exe", StringComparison.Ordinal))
    return ReexecAsExpanseExe();

string root = Path.Combine(Path.GetTempPath(), "expanse-elevation-" + Environment.ProcessId);
Directory.CreateDirectory(root);
string dataHome = Path.Combine(root, "data");
Directory.CreateDirectory(dataHome);
// .NET 10.0.15 maps SpecialFolder.LocalApplicationData to XDG_DATA_HOME on Unix
// (Environment.GetFolderPathCore.Unix.cs). Set it before Expanse.Log reads the folder.
Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);
Environment.SetEnvironmentVariable("LOCALAPPDATA", dataHome);

string logPath = Path.Combine(root, "shell.log");
string markerPath = Path.Combine(root, "marker.bin");
File.WriteAllBytes(markerPath, System.Text.Encoding.ASCII.GetBytes(MarkerText));
File.WriteAllText("/tmp/expanse-elevation-shell.conf",
    "log=" + logPath + "\n" +
    "marker=" + markerPath + "\n" +
    "off_verb=" + Marshal.OffsetOf<SHELLEXECUTEINFO>(nameof(SHELLEXECUTEINFO.lpVerb)) + "\n" +
    "off_file=" + Marshal.OffsetOf<SHELLEXECUTEINFO>(nameof(SHELLEXECUTEINFO.lpFile)) + "\n");

string stub = CompileStub(root);
var asm = Assembly.Load("Expanse");
NativeLibrary.SetDllImportResolver(asm, (name, _, _) =>
    name is "shell32.dll" or "kernel32.dll" or "advapi32.dll" or "wintrust.dll"
        ? NativeLibrary.Load(stub)
        : IntPtr.Zero);

var elevation = asm.GetType("Expanse.Elevation") ?? throw new InvalidOperationException("Expanse.Elevation missing");
var settingsType = asm.GetType("Expanse.Settings") ?? throw new InvalidOperationException("Expanse.Settings missing");
var logType = asm.GetType("Expanse.Log") ?? throw new InvalidOperationException("Expanse.Log missing");
string settingsFolder = (string)logType.GetProperty("Folder", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
Directory.CreateDirectory(settingsFolder);
string settingsFile = Path.Combine(settingsFolder, "settings.json");

bool IsElevated() => (bool)elevation.GetProperty("IsElevated", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
object LoadSettings() => settingsType.GetMethod("Load", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, null)!;
bool RunAsAdmin(object settings) => (bool)settingsType.GetProperty("RunAsAdmin")!.GetValue(settings)!;

int failures = 0;
void Expect(string name, bool ok, string detail)
{
    if (ok) { Console.WriteLine($"PASS {name}"); return; }
    failures++;
    Console.WriteLine($"FAIL {name}: {detail}");
}

if (IsElevated())
{
    Console.Error.WriteLine("setup: integrity stand-in did not report medium; startup would skip elevation");
    return 2;
}

File.WriteAllText(settingsFile, "{\"RunAsAdmin\":false}");
Expect("settings flag off is read back", RunAsAdmin(LoadSettings()) == false, "RunAsAdmin was not false");
File.WriteAllText(settingsFile, "{\"RunAsAdmin\":true}");
if (RunAsAdmin(LoadSettings()) != true)
{
    Console.Error.WriteLine($"setup: settings.json at {settingsFile} did not set RunAsAdmin");
    return 2;
}
Console.WriteLine($"PASS settings flag on is read from {settingsFile}");

string before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(processPath)));
ClearLog();
Exception? startupError = null;
object? startupExit = null;
try { startupExit = asm.EntryPoint!.Invoke(null, [Array.Empty<string>()]); }
catch (TargetInvocationException e) { startupError = e.InnerException ?? e; }
Expect("startup does not runas a user-writable Expanse.exe", !RunasOfWritablePath(out string startupDetail),
    startupDetail + DescribeStartup(startupExit, startupError, before));

ClearLog();
string? directWhy = null;
Exception? directError = null;
try
{
    directWhy = (string?)elevation.GetMethod("StartElevated", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
        .Invoke(null, null);
}
catch (TargetInvocationException e) { directError = e.InnerException ?? e; }
Expect("StartElevated does not runas a user-writable path", !RunasOfWritablePath(out string directDetail),
    directDetail + $" return={(directWhy is null ? "null" : directWhy)} error={directError?.GetType().Name}");

return failures == 0 ? 0 : 1;

int ReexecAsExpanseExe()
{
    string install = Path.Combine(Path.GetTempPath(), "expanse-install-" + Environment.ProcessId);
    if (Directory.Exists(install)) Directory.Delete(install, recursive: true);
    CopyTree(AppContext.BaseDirectory, install);
    string source = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
    string destination = Path.Combine(install, "Expanse.exe");
    File.Copy(source, destination, overwrite: true);
    File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var start = new ProcessStartInfo(destination)
    {
        UseShellExecute = false,
        WorkingDirectory = install,
    };
    foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        start.Environment[(string)entry.Key] = (string)entry.Value!;
    start.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
    using var child = Process.Start(start) ?? throw new InvalidOperationException("could not start Expanse.exe");
    child.WaitForExit();
    return child.ExitCode;
}

static void CopyTree(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (string file in Directory.GetFiles(from))
        File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
    foreach (string dir in Directory.GetDirectories(from))
        CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
}

string CompileStub(string directory)
{
    string? source = FindStubSource();
    if (source == null)
    {
        Console.Error.WriteLine("setup: shell_stub.c not found");
        Environment.Exit(2);
    }
    string so = Path.Combine(directory, "shell_stub.so");
    var gcc = Process.Start(new ProcessStartInfo("gcc", $"-shared -fPIC -O2 -Wall -Wextra -o \"{so}\" \"{source}\"")
    {
        UseShellExecute = false,
        RedirectStandardError = true,
    })!;
    string err = gcc.StandardError.ReadToEnd();
    gcc.WaitForExit();
    if (gcc.ExitCode != 0)
    {
        Console.Error.WriteLine("setup: gcc failed: " + err);
        Environment.Exit(2);
    }
    return so;
}

static string? FindStubSource()
{
    string? dir = AppContext.BaseDirectory;
    while (dir != null)
    {
        string candidate = Path.Combine(dir, "native", "shell_stub.c");
        if (File.Exists(candidate)) return candidate;
        candidate = Path.Combine(dir, "tests", "Elevation.Tests", "native", "shell_stub.c");
        if (File.Exists(candidate)) return candidate;
        dir = Path.GetDirectoryName(dir);
    }
    return null;
}

void ClearLog()
{
    File.WriteAllText(logPath, "");
}

bool RunasOfWritablePath(out string detail)
{
    detail = "";
    if (!File.Exists(logPath)) { detail = "no shell log"; return false; }
    foreach (string line in File.ReadAllLines(logPath))
    {
        if (line.Length == 0) continue;
        var parts = line.Split('\t');
        string verb = Part(parts, "verb");
        string path = Part(parts, "path");
        bool writable = Part(parts, "writable") == "1";
        bool replaced = Part(parts, "replaced") == "1";
        bool admin = Part(parts, "admin") == "1";
        if (!verb.Equals("runas", StringComparison.OrdinalIgnoreCase) || !admin) continue;
        byte[] now = File.Exists(path) ? File.ReadAllBytes(path) : [];
        bool marker = now.AsSpan().SequenceEqual(System.Text.Encoding.ASCII.GetBytes(MarkerText));
        detail = $"verb={verb} path={path} writable={writable} replaced={replaced} use-time marker={marker}";
        if (writable) return true;
    }
    detail = File.ReadAllText(logPath).Trim();
    if (detail.Length == 0) detail = "ShellExecuteEx was not called";
    return false;
}

static string Part(string[] parts, string key)
{
    string prefix = key + "=";
    foreach (string part in parts)
        if (part.StartsWith(prefix, StringComparison.Ordinal)) return part[prefix.Length..];
    return "";
}

static string DescribeStartup(object? exit, Exception? error, string beforeHash)
{
    string tail = $" exit={(exit is null ? "threw" : exit)}";
    if (error != null) tail += $" after={error.GetType().Name}: {error.Message}";
    tail += $" image-before={beforeHash[..12]}";
    return tail;
}
