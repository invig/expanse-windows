using Expanse;
using Expanse.Native;
using static Expanse.Native.User32;

if (args.Contains("--restore"))
{
    Originals.RestoreFromFile();
    return 0;
}

using var mutex = new Mutex(true, @"Local\Expanse", out bool first);
if (!first)
{
    Log.Info("another Expanse is already running");
    return 1;
}

Log.Info($"Expanse starting (pid {Environment.ProcessId})");
Originals.RestoreFromFile();

// --pictures dwm: DWM thumbnails instead of shared visuals (which fall back to them anyway).
int kindAt = Array.IndexOf(args, "--pictures");
string pictureKind = kindAt >= 0 && kindAt + 1 < args.Length ? args[kindAt + 1] : "shared";

var overlay = new Overlay();
var canvas = new Canvas(overlay, pictureKind);
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Log.Info($"crashed: {e.ExceptionObject}");
    canvas.Quit();
};

// Ctrl+Alt + the key below Esc (` on a US layout, ` ¬ on a UK one), found by scan code so it's
// the same key on every layout. Alt+` alone is the Japanese IME toggle.
uint vk = MapVirtualKey(0x29, MAPVK_VSC_TO_VK);
bool hotkey = RegisterHotKey(overlay.Hwnd, Canvas.HotkeyCanvas, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, vk);
Log.Info(hotkey ? $"hotkey Ctrl+Alt+VK 0x{vk:X2}" : "couldn't register Ctrl+Alt+`: something else has it");
// Hops: Ctrl+Alt+arrows (Ctrl+arrows is word-jump everywhere, Win+arrows is Snap).
foreach (var (id, key, name) in new[] { (Canvas.HotkeyLeft, VK_LEFT, "Left"), (Canvas.HotkeyRight, VK_RIGHT, "Right"),
                                         (Canvas.HotkeyUp, VK_UP, "Up"), (Canvas.HotkeyDown, VK_DOWN, "Down") })
    if (!RegisterHotKey(overlay.Hwnd, id, MOD_CONTROL | MOD_ALT, (uint)key))
        Log.Info($"couldn't register Ctrl+Alt+{name}: something else has it");

new CommandServer(overlay.Hwnd).Start();
canvas.Start();

// The message loop. While the camera animates it runs a frame at a time: handle whatever
// input is waiting, move the pictures, then wait for DWM to compose (DwmFlush).
while (true)
{
    if (canvas.IsAnimating)
    {
        while (PeekMessage(out var m, 0, 0, 0, PM_REMOVE))
        {
            if (m.message == WM_QUIT) return 0;
            TranslateMessage(ref m);
            DispatchMessage(ref m);
        }
        canvas.Tick();
        if (canvas.IsAnimating) Dwm.DwmFlush();
    }
    else
    {
        if (GetMessage(out var m, 0, 0, 0) <= 0) break;
        TranslateMessage(ref m);
        DispatchMessage(ref m);
    }
}
Log.Info("Expanse quit");
return 0;
