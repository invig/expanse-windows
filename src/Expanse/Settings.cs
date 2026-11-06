using System.Text.Json;

namespace Expanse;

/// The choices in the tray panel, kept in %LOCALAPPDATA%\Expanse\settings.json.
sealed class Settings
{
    /// A window that gets focus while it isn't fully on screen (from the taskbar, Alt-Tab, or an
    /// app) glides into view, as on the Mac.
    public bool AutoCentre { get; set; } = true;

    /// The desktop picture behind the canvas; off, a plain dark background.
    public bool Wallpaper { get; set; } = true;

    /// Hot Corner: which corner of the main screen zooms out when the pointer rests there (see
    /// HotCorner for the choices; every corner to begin with, as on the Mac).
    public string HotCorner { get; set; } = Expanse.HotCorner.Default;

    /// Show Window Names: a label over each picture zoomed out (the app's name; the window's title
    /// too on hover). Off, as on the Mac, where titles can be private.
    public bool ShowNames { get; set; }

    /// Include Admin Windows: Expanse runs as administrator (Windows asks each time it starts) so
    /// it can move admin windows too. See Elevation.
    public bool RunAsAdmin { get; set; }

    /// Troubleshooting > Detailed Logging: the log is written to a file too (see Log).
    public bool DetailedLogging { get; set; }

    /// Welcome & Shortcuts has been shown (it opens by itself on first launch only).
    public bool WelcomeShown { get; set; }

    /// The note about admin windows staying put has been shown (once ever).
    public bool AdminNoteShown { get; set; }

    /// Check for Updates Automatically (release builds): once a day, as on the Mac.
    public bool CheckForUpdates { get; set; } = true;

    /// The update the tray icon last said was ready (said once per version).
    public string? UpdateNoted { get; set; }

    /// Moving around: "follow" jumps when Windows' Animation effects are off, "glide" always
    /// animates, "jump" goes straight to where a move ends (the Mac's Low motion).
    public string Motion { get; set; } = "follow";

    /// Gaps between windows: 20 (the default), 10 or none (see Layout.Gap).
    public double WindowGap { get; set; } = 20;

    /// The gap, if it's one of the three: what's in the file is yours to edit.
    public double ValidGap => WindowGap is 0 or 10 ? WindowGap : 20;

    static string File => Path.Combine(Log.Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new Settings();
        }
        catch (Exception e) { Log.Info($"settings: couldn't read ({e.Message}); using defaults"); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this));
        }
        catch (IOException e) { Log.Info($"settings: couldn't save: {e.Message}"); }
    }
}
