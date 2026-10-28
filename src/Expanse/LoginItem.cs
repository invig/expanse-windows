using Microsoft.Win32;

namespace Expanse;

/// Open at Login (tray): Expanse starts when you sign in, from the Run key under your account.
/// With Include Admin Windows on, that start asks to run as administrator (see Program).
static class LoginItem
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Expanse";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsOn
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(Key);
            return run?.GetValue(Name) is string s && s.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool on)
    {
        using var run = Registry.CurrentUser.CreateSubKey(Key);
        if (on) run.SetValue(Name, Command);
        else run.DeleteValue(Name, throwOnMissingValue: false);
        Log.Info($"open at login {(on ? "on" : "off")}");
    }
}
