using System.IO.Pipes;
using System.Web;
using Microsoft.Win32;

namespace Expanse;

/// `expanse://` links, for scripting, as on the Mac: `start expanse://tidy` and the like. Expanse
/// registers the scheme for your account when it starts; a link starts a second Expanse that hands
/// the link to the running one over the command pipe (see CommandServer) and quits.
///
///   toggle, enter, exit, tidy, centre, fit, restore (Restore All Windows), pause, resume,
///   nav?dir=left|right|up|down, focus?app=NAME, layout?snapshot=N, zoom?scale=S, pan?dx=X&dy=Y
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
        string action = (uri.Host.Length > 0 ? uri.Host : uri.AbsolutePath.Trim('/')).ToLowerInvariant();
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
            "nav" when q["dir"] is string d => $"hop {d}",
            "focus" when q["app"] is string a => $"focus {a}",
            "layout" when q["snapshot"] is string s => $"restore {s}",
            "zoom" when q["scale"] is string s => $"zoom {s}",
            "pan" => $"pan {q["dx"] ?? "0"} {q["dy"] ?? "0"}",
            "update" => q["install"] == "1" ? "update install" : "update",
            _ => null,
        };
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
