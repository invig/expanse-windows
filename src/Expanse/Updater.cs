using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Expanse;

/// Updates straight from Expanse's GitHub releases, as on the Mac, with no other service involved:
/// a minute after launch and then once a day it asks GitHub which release is the latest, and when
/// that's newer, downloads the zip for this PC's kind of processor and checks it. Installing swaps
/// the new Expanse.exe in for this one (Windows lets a running program's file be renamed, not
/// replaced) and restarts, handing over as Include Admin Windows does, so windows stay where they
/// are. Release builds only (scripts/release.sh): a development build never updates.
///
/// The checks: the zip's SHA-256 is the one GitHub lists for it, and the Expanse.exe in it says
/// it's the version GitHub said. A copy that's code-signed takes only an update signed by the same
/// publisher (Authenticode), the counterpart of the Mac's Developer ID check; an unsigned one
/// (there's no signing yet) rests on HTTPS from GitHub, as downloading it yourself does.
sealed class Updater(Action<Action> post)
{
    public sealed record Release(string Version, Uri Zip, Uri Page, string? Sha256);
    public enum Outcome { DevelopmentBuild, Busy, Failed, UpToDate, Ready }

    public const string Repo = "invig/expanse-windows";
    static readonly Uri LatestUrl = new($"https://api.github.com/repos/{Repo}/releases/latest");

    /// Built by scripts/release.sh (ExpanseRelease set): a release, which has releases to update to.
    public static readonly bool IsRelease = typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "ExpanseRelease" && a.Value == "true");

    /// "0.1.3": the version this copy says, without the commit the SDK adds after a +.
    public static readonly string CurrentVersion = Plain(typeof(Updater).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    static readonly HttpClient http = MakeClient();

    /// Downloaded and checked, waiting to be installed: the release and its Expanse.exe.
    public (Release release, string exe)? Ready { get; private set; }
    /// Called on the UI thread when an update has been downloaded and checked.
    public Action<Release>? OnReady { get; set; }
    /// For testing (`update-source URL`): somewhere else to ask for the latest release, any host.
    public Uri? Source { get; set; }
    bool checking;
    Timer? timer;

    static HttpClient MakeClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Expanse/{CurrentVersion}");
        return client;
    }

    /// Checks a minute after launch and then once a day, whenever `automatic()` says to.
    public void Start(Func<bool> automatic)
    {
        if (!IsRelease) return;
        timer = new Timer(_ => post(() => { if (automatic()) Check(); }), null, TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));
    }

    /// Asks GitHub for the latest release and, when it's newer, downloads it. `done` hears how
    /// it went, in words, on the UI thread.
    public void Check(Action<Outcome, string>? done = null)
    {
        if (!IsRelease)
        {
            done?.Invoke(Outcome.DevelopmentBuild, "This copy of Expanse is a development build.");
            return;
        }
        if (checking)
        {
            done?.Invoke(Outcome.Busy, "Already checking.");
            return;
        }
        checking = true;
        var (source, have) = (Source, Ready);
        Task.Run(async () =>
        {
            (Outcome outcome, string text, Release? release, string? exe) result;
            try { result = await Fetch(source, have); }
            catch (Exception e) { result = (Outcome.Failed, e.Message, null, null); }
            post(() =>
            {
                checking = false;
                var (outcome, text, release, exe) = result;
                Log.Info($"update check: {outcome} ({text})");
                if (outcome == Outcome.Ready && release != null && exe != null)
                {
                    bool fresh = Ready?.exe != exe;
                    Ready = (release, exe);
                    if (fresh) OnReady?.Invoke(release);
                }
                done?.Invoke(outcome, text);
            });
        });
    }

    async Task<(Outcome, string, Release?, string?)> Fetch(Uri? source, (Release release, string exe)? have)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source ?? LatestUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request);
        // GitHub says 404 until there's a release (and while the repo is private).
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (Outcome.Failed, "GitHub has no release of Expanse for Windows yet.", null, null);
        if (!response.IsSuccessStatusCode)
            return (Outcome.Failed, $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, null);
        if (Parse(await response.Content.ReadAsStringAsync(), anyHost: source != null) is not Release release)
            return (Outcome.Failed, "GitHub didn't say which release is the latest.", null, null);
        if (!IsNewer(release.Version, CurrentVersion))
            return (Outcome.UpToDate, $"Expanse {CurrentVersion} is the latest version.", release, null);
        // Already downloaded and waiting (a newer one since means downloading that).
        if (have is { } h && h.release.Version == release.Version && File.Exists(h.exe))
            return (Outcome.Ready, $"Expanse {release.Version} is ready to install.", h.release, h.exe);
        return await Download(release) is string exe
            ? (Outcome.Ready, $"Expanse {release.Version} is ready to install.", release, exe)
            : (Outcome.Failed, $"Couldn't download Expanse {release.Version}. Try again later, or download it from GitHub.", release, null);
    }

    /// The latest release's version and the zip for this PC, from GitHub's answer.
    static Release? Parse(string json, bool anyHost)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            string version = tag.StartsWith('v') ? tag[1..] : tag;
            // Digits and dots only: it's compared, and named in what's downloaded.
            if (version.Length == 0 || !version.All(c => char.IsAsciiDigit(c) || c == '.')) return null;
            var page = new Uri(root.GetProperty("html_url").GetString() ?? "");
            string name = $"Expanse-{version}-windows-{Arch}.zip";
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != name) continue;
                var zip = new Uri(asset.GetProperty("browser_download_url").GetString() ?? "");
                if (!anyHost && (zip.Scheme != Uri.UriSchemeHttps || zip.Host != "github.com")) return null;
                string? digest = asset.TryGetProperty("digest", out var d) && d.GetString() is string s && s.StartsWith("sha256:") ? s[7..] : null;
                return new Release(version, zip, page, digest);
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or UriFormatException) { }
        return null;
    }

    /// "0.1.10" is newer than "0.1.9".
    public static bool IsNewer(string a, string than)
    {
        var x = a.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
        var y = than.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
            if (p != q) return p > q;
        }
        return false;
    }

    static string Plain(string? version) => (version ?? "0").Split('+')[0].Trim();

    /// Downloads and unpacks `release`, and hands back its Expanse.exe if it checks out.
    static async Task<string?> Download(Release release)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"Expanse-update-{Guid.NewGuid():N}");
        string zip = Path.Combine(folder, "update.zip"), exe = Path.Combine(folder, "Expanse.exe");
        bool good = false;
        try
        {
            Directory.CreateDirectory(folder);
            using (var response = await http.GetAsync(release.Zip, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(zip);
                await response.Content.CopyToAsync(file);
            }
            if (release.Sha256 is string expected)
            {
                string actual;
                using (var file = File.OpenRead(zip)) actual = Convert.ToHexStringLower(SHA256.HashData(file));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"update {release.Version}: the download isn't the file GitHub lists (SHA-256 {actual})");
                    return null;
                }
            }
            using (var archive = ZipFile.OpenRead(zip))
            {
                if (archive.GetEntry("Expanse.exe") is not ZipArchiveEntry entry) return null;
                entry.ExtractToFile(exe);
            }
            File.Delete(zip);
            good = Verify(exe, release.Version);
            return good ? exe : null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException)
        {
            Log.Info($"update download failed: {e.Message}");
            return null;
        }
        finally
        {
            // Nothing to keep from a download that didn't check out.
            if (!good) TryDelete(folder);
        }
    }

    /// The version GitHub said, and when this copy is signed, signed by the same publisher.
    static bool Verify(string exe, string version)
    {
        string bundled = Plain(FileVersionInfo.GetVersionInfo(exe).ProductVersion);
        string? mine = Publisher(Environment.ProcessPath!);
        bool signature = mine == null || Publisher(exe) == mine;
        Log.Info($"update {version} checks: version {bundled}, signature " +
                 (mine == null ? "not needed (this copy is unsigned)" : signature ? "same publisher" : "not the same publisher"));
        return bundled == version && signature;
    }

    /// Swaps the downloaded Expanse.exe in for this one. Null when it's in place (the caller
    /// restarts); otherwise why not, with the release open on GitHub instead (where this copy
    /// can't be replaced: a folder you can't write to).
    public string? Install()
    {
        if (Ready is not { } ready) return "There's no update waiting.";
        string current = Environment.ProcessPath!;
        string folder = Path.GetDirectoryName(current)!;
        string staged = Path.Combine(folder, "Expanse.update.exe"), old = Path.Combine(folder, "Expanse.old.exe");
        try
        {
            File.Copy(ready.exe, staged, overwrite: true);
            File.Delete(old);
            File.Move(current, old);
            try { File.Move(staged, current); }
            catch { File.Move(old, current); throw; }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Info($"update install failed: {e.Message}");
            try { File.Delete(staged); } catch (Exception) { }
            Open(ready.release.Page);
            return e.Message;
        }
        Log.Info($"installed {ready.release.Version}; restarting");
        TryDelete(Path.GetDirectoryName(ready.exe)!);
        Ready = null;
        return null;
    }

    /// What an update leaves beside Expanse.exe: the copy it replaced, still running then. That
    /// one may take a moment to finish exiting after handing over.
    public static void CleanUp()
    {
        string folder = Path.GetDirectoryName(Environment.ProcessPath!)!;
        foreach (var path in new[] { "Expanse.old.exe", "Expanse.update.exe" }.Select(n => Path.Combine(folder, n)).Where(File.Exists))
            Task.Run(async () =>
            {
                for (int i = 0; i < 20 && File.Exists(path); i++)
                {
                    try { File.Delete(path); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(500); }
                }
            });
    }

    static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// A web page in your browser. Run as administrator, through Explorer, so the browser isn't.
    public static void Open(Uri page)
    {
        if (Elevation.IsElevated) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{page}\"") { UseShellExecute = false });
        else Process.Start(new ProcessStartInfo(page.ToString()) { UseShellExecute = true });
    }

    // MARK: Authenticode

    /// The publisher a program is signed by, if it's signed and the signature holds.
    static string? Publisher(string path)
    {
        if (!Trusted(path)) return null;
#pragma warning disable SYSLIB0057 // nothing replaces reading the certificate a signed file carries
        try { return X509Certificate.CreateFromSignedFile(path).Subject; }
#pragma warning restore SYSLIB0057
        catch (CryptographicException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_FILE_INFO
    {
        public int cbStruct;
        public nint pcwszFilePath, hFile, pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public int cbStruct;
        public nint pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public nint pSignatureSettings;
    }

    [DllImport("wintrust.dll")]
    static extern int WinVerifyTrust(nint hwnd, ref Guid action, ref WINTRUST_DATA data);

    /// Signed, with a signature Windows trusts (WinVerifyTrust, no UI).
    static bool Trusted(string path)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        nint name = Marshal.StringToHGlobalUni(path);
        var file = new WINTRUST_FILE_INFO { cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = name };
        nint filePtr = Marshal.AllocHGlobal(file.cbStruct);
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,    // WTD_UI_NONE
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = filePtr,
            };
            return WinVerifyTrust(-1, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(name);
        }
    }
}
