namespace Expanse;

/// Timestamped lines to %LOCALAPPDATA%\Expanse\expanse.log, plus the last few hundred in
/// memory for the `log` command.
static class Log
{
    static readonly object gate = new();
    static readonly Queue<string> recent = new();
    static StreamWriter? file;

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Expanse");

    public static void Info(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (gate)
        {
            try
            {
                if (file == null)
                {
                    Directory.CreateDirectory(Folder);
                    file = new StreamWriter(Path.Combine(Folder, "expanse.log"), append: true) { AutoFlush = true };
                }
                file.WriteLine(line);
            }
            catch (IOException) { }
            recent.Enqueue(line);
            while (recent.Count > 400) recent.Dequeue();
        }
    }

    public static string Recent(int n)
    {
        lock (gate) return string.Join("\n", recent.Skip(Math.Max(0, recent.Count - n)));
    }
}
