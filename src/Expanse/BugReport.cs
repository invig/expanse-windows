using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// Tray menu > Troubleshooting > Copy Logs for a Bug Report: what's needed to look into a problem,
/// on the clipboard, ready to paste into a GitHub issue. This PC and Expanse's settings, then the log:
/// the Detailed Logging file, or without it, this run's lines (kept in memory). As on the Mac.
static class BugReport
{
    /// Troubleshooting > Report an Issue on GitHub…
    public static readonly Uri IssuesUrl = new($"https://github.com/{Updater.Repo}/issues/new");

    /// GitHub takes 65,536 characters in an issue; this leaves room for the summary and your words.
    const int MaxLogCharacters = 45_000;

    /// `settings`: Expanse's own state, one line each. False if the clipboard was busy.
    public static bool Copy(nint owner, IEnumerable<string> settings)
    {
        string file = "";
        if (Log.ToFile && File.Exists(Log.FilePath))
        {
            try
            {
                using var reader = new StreamReader(new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                file = reader.ReadToEnd();
                if (file.Length > MaxLogCharacters) file = file[^MaxLogCharacters..];
            }
            catch (IOException) { }
        }
        string log = file.Length > 0 ? file : Log.RecentText(MaxLogCharacters);
        string source = file.Length > 0 ? "Detailed log" : "Log for this run (Detailed Logging is off: turn it on for more)";
        string report = $"### Expanse\n{Summary(settings)}\n<details><summary>{source}</summary>\n\n```\n" +
                        $"{(log.Length == 0 ? "(nothing logged)" : log)}\n```\n</details>\n";
        return Clipboard.SetText(owner, report);
    }

    static string Summary(IEnumerable<string> settings)
    {
        var asm = Assembly.GetExecutingAssembly();
        string version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "?";
        using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        // ProductName still says Windows 10 on Windows 11: the build tells them apart.
        string product = $"{nt?.GetValue("ProductName")}";
        if (int.TryParse($"{nt?.GetValue("CurrentBuild")}", out int build) && build >= 22000) product = product.Replace("Windows 10", "Windows 11");
        string windows = $"{product} {nt?.GetValue("DisplayVersion")} (build {build}.{nt?.GetValue("UBR")}), {RuntimeInformation.OSArchitecture}";
        var displays = Monitors().Select(m => $"{m.monitor.Width}x{m.monitor.Height}{(m.primary ? " (main)" : "")}");
        var lines = new[]
        {
            $"Expanse {version} ({(Updater.IsRelease ? "release" : "development")} build), {(Elevation.IsElevated ? "running as administrator" : "running as you")}",
            $"Windows: {windows}",
            $"Displays: {string.Join("; ", displays)}, main scaling {PrimaryDpi() * 100 / 96}%",
        }.Concat(settings);
        return string.Join("\n", lines.Select(l => $"- {l}"));
    }
}
