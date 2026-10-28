using System.Text.Json;

namespace Expanse;

/// The choices on the tray menu, kept in %LOCALAPPDATA%\Expanse\settings.json.
sealed class Settings
{
    /// A window that gets focus while it isn't fully on screen (from the taskbar, Alt-Tab, or an
    /// app) glides into view, as on the Mac.
    public bool AutoCentre { get; set; } = true;

    /// The desktop picture behind the canvas; off, a plain dark background.
    public bool Wallpaper { get; set; } = true;

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
