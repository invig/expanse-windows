namespace Expanse;

/// Timestamped lines: the last couple of thousand in memory (for `ctl log` and Copy Logs for a Bug
/// Report), and with Detailed Logging on (the tray's Troubleshooting menu) in
/// %LOCALAPPDATA%\Expanse\expanse.log too. As on the Mac, what Expanse did with which app's
/// windows, never their titles: callers log windows by app and handle (CanvasWindow.ToString).
static class Log
{
    static readonly object gate = new();
    static readonly Queue<string> recent = new();
    static StreamWriter? file;
    static bool toFile;

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Expanse");

    public static string FilePath => Path.Combine(Folder, "expanse.log");

    /// Detailed Logging: the file is written too, kept under 5 MB (the rest moves to
    /// expanse.old.log).
    public static bool ToFile
    {
        get => toFile;
        set
        {
            lock (gate)
            {
                toFile = value;
                if (!value) { file?.Dispose(); file = null; }
            }
        }
    }

    /// Turned on from the menu: a fresh file, so a bug report has this session, not old ones.
    public static void StartFresh()
    {
        lock (gate)
        {
            file?.Dispose();
            file = null;
            try { if (File.Exists(FilePath)) File.Move(FilePath, Path.Combine(Folder, "expanse.old.log"), overwrite: true); }
            catch (IOException) { }
        }
    }

    public static void Info(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (gate)
        {
            if (toFile)
            {
                try
                {
                    if (file == null)
                    {
                        Directory.CreateDirectory(Folder);
                        if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 5_000_000)
                            File.Move(FilePath, Path.Combine(Folder, "expanse.old.log"), overwrite: true);
                        file = new StreamWriter(FilePath, append: true) { AutoFlush = true };
                    }
                    file.WriteLine(line);
                }
                catch (IOException) { }
            }
            recent.Enqueue(line);
            while (recent.Count > 2000) recent.Dequeue();
        }
    }

    public static string Recent(int n)
    {
        lock (gate) return string.Join("\n", recent.Skip(Math.Max(0, recent.Count - n)));
    }

    /// This run's lines, newest last, at most `maxCharacters` of the newest.
    public static string RecentText(int maxCharacters)
    {
        lock (gate)
        {
            var text = string.Join("\n", recent);
            return text.Length <= maxCharacters ? text : text[^maxCharacters..];
        }
    }
}
