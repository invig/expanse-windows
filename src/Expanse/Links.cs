using System.Diagnostics;
using System.IO.Pipes;
using System.Web;
using Expanse.Native;
using Microsoft.Win32;

namespace Expanse;

/// `expanse://` links, for scripting, as on the Mac: `start expanse://tidy` and the like. Expanse
/// registers the scheme for your account when it starts; a link starts a second Expanse that hands
/// the link to the running one over the command pipe (see CommandServer) and quits.
///
///   toggle, enter, exit, tidy, centre, fit, restore (Restore All Windows), pause, resume, welcome,
///   menu, nav?dir=left|right|up|down, focus?app=NAME, layout?snapshot=N, zoom?scale=S, pan?dx=X&dy=Y
///
/// Any web page can open these once you let your browser. A link from one runs only after Expanse
/// asks, and whatever a link says is checked: known commands, and numbers within reason.
static class Links
{
    public const string Scheme = "expanse";

    /// The scheme points at this Expanse (written only when it doesn't already).
    public static void Register()
    {
        string command = $"\"{Environment.ProcessPath}\" \"%1\"";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            using var open = key.CreateSubKey(@"shell\open\command");
            if (open.GetValue("") as string == command) return;
            key.SetValue("", "URL:Expanse");
            key.SetValue("URL Protocol", "");
            open.SetValue("", command);
            Log.Info("expanse:// links registered");
        }
        catch (Exception e) { Log.Info($"expanse:// links: couldn't register ({e.Message})"); }
    }

    /// The pipe command a link stands for, or null if it isn't one Expanse knows.
    public static string? Command(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Scheme) return null;
        var q = HttpUtility.ParseQueryString(uri.Query);
        string action = Action(uri);
        // A name given twice comes back joined by a comma: only the first counts.
        string? One(string key) => q[key]?.Split(',')[0];
        // Finite numbers only, and within reason: "nan" and "inf" parse as numbers too.
        static double? Number(string? v) => double.TryParse(v, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) && Math.Abs(d) <= 1_000_000 ? d : null;
        string N(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return action switch
        {
            "toggle" => "toggle",
            "enter" => "zoomout",
            "exit" => "back",
            "tidy" => "tidy",
            "centre" or "center" => "centre",
            "fit" => "fit",
            "restore" => "restoreall",
            "pause" => "pause",
            "resume" => "resume",
            "welcome" => "welcome",
            "menu" => "menu",
            "nav" when One("dir") is "left" or "right" or "up" or "down" => $"hop {One("dir")}",
            "focus" when One("app") is { Length: > 0 } a => $"focus {a}",
            "layout" when q["snapshot"] == null => "restore 0",
            "layout" when int.TryParse(One("snapshot"), out int n) && n is >= 0 and <= 10_000 => $"restore {n}",
            "zoom" when Number(One("scale")) is double s && s > 0 => $"zoom {N(s)}",
            // A number that isn't one means the link is wrong: it does nothing.
            "pan" when Number(One("dx") ?? "0") is double dx && Number(One("dy") ?? "0") is double dy => $"pan {N(dx)} {N(dy)}",
            // Debugging: development builds only, since any web page can open these links.
            "update" when !Updater.IsRelease => One("install") == "1" ? "update install" : "update",
            _ => null,
        };
    }

    static string Action(Uri uri) => (uri.Host.Length > 0 ? uri.Host : uri.AbsolutePath.Trim('/')).ToLowerInvariant();

    /// The web browser this link came from, by name: the program that started this copy of Expanse,
    /// if it opens web pages. Null for anything else (a script, the Run box, another app).
    static string? BrowserSending()
    {
        try
        {
            if (Kernel32.ParentOf(Kernel32.GetCurrentProcessId()) is not uint parent || Kernel32.ProcessPath(parent) is not string path) return null;
            string exe = Path.GetFileNameWithoutExtension(path);
            if (!knownBrowsers.Contains(exe) && !RegisteredBrowsers().Contains(path)) return null;
            string? name = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            return string.IsNullOrEmpty(name) ? exe : name;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    static readonly HashSet<string> knownBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "arc", "iexplore", "chromium", "waterfox", "librewolf", "zen",
    };

    /// The programs Windows knows as web browsers (Settings > Default apps lists them).
    static HashSet<string> RegisteredBrowsers()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var clients = hive.OpenSubKey(@"Software\Clients\StartMenuInternet");
            foreach (string name in clients?.GetSubKeyNames() ?? [])
            {
                using var open = clients!.OpenSubKey($@"{name}\shell\open\command");
                if (open?.GetValue("") is string command) found.Add(command.Trim().Split('"', StringSplitOptions.RemoveEmptyEntries)[0].Trim());
            }
        }
        return found;
    }

    /// Scripts and apps run these links as they are. A web page can open one too, once you let the
    /// browser, and could then rearrange your windows or switch apps while you type into one: so
    /// you're asked, each time. Welcome and the panel show you something, and move nothing.
    static bool Allowed(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || Action(uri) is "welcome" or "menu") return true;
        if (BrowserSending() is not string browser) return true;
        const uint MB_YESNO = 0x4, MB_ICONWARNING = 0x30, MB_DEFBUTTON2 = 0x100, MB_TOPMOST = 0x40000, MB_SETFOREGROUND = 0x10000;
        bool allow = Expanse.Native.User32.MessageBox(0,
            $"A web page in {browser} wants Expanse to run \u201c{Action(uri)}\u201d.\n\n" +
            "Links from web pages can move your windows and switch apps. Allow it only if you clicked the link yourself, expecting this.\n\nAllow it?",
            "Expanse", MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2 | MB_TOPMOST | MB_SETFOREGROUND) == 6 /* IDYES */;
        Log.Info($"link from a web page in {browser}: {Action(uri)} {(allow ? "allowed" : "not allowed")}");
        return allow;
    }

    /// Hands a link to the running Expanse. False if there isn't one, or it won't listen (run
    /// as administrator, it takes commands only from admin programs).
    public static bool Forward(string link)
    {
        if (Command(link) is not string command)
        {
            Log.Info($"expanse:// link not understood");
            return false;
        }
        if (!Allowed(link)) return true;
        try
        {
            using var pipe = new NamedPipeClientStream(".", CommandServer.PipeName, PipeDirection.InOut);
            pipe.Connect(3000);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine(command);
            new StreamReader(pipe).ReadToEnd();
            return true;
        }
        catch (Exception e)
        {
            Log.Info($"expanse:// link: couldn't reach Expanse ({e.GetType().Name})");
            return false;
        }
    }
}
