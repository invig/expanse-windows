using System.Text.Json;

namespace Spikes;

/// Where results go: out\spikes.log, and out\spike-NAME.json.
static class Out
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "expanse-windows", "out");

    public static void Log(string s)
    {
        Directory.CreateDirectory(Folder);
        File.AppendAllText(Path.Combine(Folder, "spikes.log"), $"{DateTime.Now:HH:mm:ss.fff} {s}\n");
    }

    public static void Result(string name, object value)
    {
        Directory.CreateDirectory(Folder);
        string json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(Folder, $"spike-{name}.json"), json);
        Log($"{name}: {json}");
    }

    public static double Percentile(List<double> sorted, double q) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];
}
