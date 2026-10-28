using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Expanse.Native;
using static Expanse.Native.User32;

// Guest: test helper that runs on the VM's desktop (started by scripts/guest/desk.ps1, since
// programs started over SSH can't see or touch the desktop). Steps run in order:
//   shot PATH        screenshot of the primary screen, as PNG
//   move X Y         pointer to X,Y (physical pixels)
//   click X Y        move there and left-click
//   rclick X Y       move there and right-click
//   drag X1 Y1 X2 Y2 press at X1,Y1, move to X2,Y2 in steps, release
//   down / up        press / release the left button where the pointer is
//   res W H          change the screen's resolution
//   ctrl+drag ...    the same drag with Ctrl held
//   key COMBO        e.g. ctrl+alt+grave, esc, alt+tab (sent as scan codes)
//   wait MS
// Results and errors go to out\guest.log (a desktop program has nowhere else to print).
string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "expanse-windows", "out", "guest.log");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
void Log(string s) => File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {s}\n");

var (monitor, _) = PrimaryMonitor();
try
{
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "shot": Shot(args[++i]); break;
            case "move": Move(int.Parse(args[++i]), int.Parse(args[++i])); break;
            case "click":
                Move(int.Parse(args[++i]), int.Parse(args[++i]));
                Thread.Sleep(30);
                Mouse(MOUSEEVENTF_LEFTDOWN);
                Thread.Sleep(40);
                Mouse(MOUSEEVENTF_LEFTUP);
                Log($"click {args[i - 1]},{args[i]}");
                break;
            case "rclick":
                Move(int.Parse(args[++i]), int.Parse(args[++i]));
                Thread.Sleep(30);
                Mouse(MOUSEEVENTF_RIGHTDOWN);
                Thread.Sleep(40);
                Mouse(MOUSEEVENTF_RIGHTUP);
                Log($"rclick {args[i - 1]},{args[i]}");
                break;
            case "drag":
            {
                int x1 = int.Parse(args[++i]), y1 = int.Parse(args[++i]), x2 = int.Parse(args[++i]), y2 = int.Parse(args[++i]);
                Move(x1, y1);
                Thread.Sleep(50);
                Mouse(MOUSEEVENTF_LEFTDOWN);
                for (int step = 1; step <= 20; step++)
                {
                    Thread.Sleep(15);
                    Move(x1 + (x2 - x1) * step / 20, y1 + (y2 - y1) * step / 20);
                }
                Thread.Sleep(50);
                Mouse(MOUSEEVENTF_LEFTUP);
                Log($"drag {x1},{y1} -> {x2},{y2}");
                break;
            }
            case "res":
            {
                int w = int.Parse(args[++i]), h = int.Parse(args[++i]);
                var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>(), dmDeviceName = "", dmFormName = "" };
                EnumDisplaySettings(null, -1, ref mode); // ENUM_CURRENT_SETTINGS
                mode.dmPelsWidth = w;
                mode.dmPelsHeight = h;
                mode.dmFields = 0x80000 | 0x100000; // DM_PELSWIDTH | DM_PELSHEIGHT
                int result = ChangeDisplaySettingsEx(null, ref mode, 0, 0, 0);
                Log($"res {w}x{h}: {result}");
                if (result != 0) throw new InvalidOperationException($"ChangeDisplaySettingsEx returned {result}");
                break;
            }
            case "down": Mouse(MOUSEEVENTF_LEFTDOWN); Log("down"); break;
            case "up": Mouse(MOUSEEVENTF_LEFTUP); Log("up"); break;
            case "ctrl+drag":
            {
                int x1 = int.Parse(args[++i]), y1 = int.Parse(args[++i]), x2 = int.Parse(args[++i]), y2 = int.Parse(args[++i]);
                Send(KeyInput(0x1D, up: false));
                Move(x1, y1);
                Thread.Sleep(50);
                Mouse(MOUSEEVENTF_LEFTDOWN);
                for (int step = 1; step <= 20; step++)
                {
                    Thread.Sleep(15);
                    Move(x1 + (x2 - x1) * step / 20, y1 + (y2 - y1) * step / 20);
                }
                Thread.Sleep(50);
                Mouse(MOUSEEVENTF_LEFTUP);
                Send(KeyInput(0x1D, up: true));
                Log($"ctrl+drag {x1},{y1} -> {x2},{y2}");
                break;
            }
            case "key": Key(args[++i]); break;
            // Held across the steps that follow (Shift-click, Space-drag, Ctrl+wheel).
            case "hold": Send(KeyInput(ScanCode(args[++i]), up: false)); Log($"hold {args[i]}"); break;
            case "release": Send(KeyInput(ScanCode(args[++i]), up: true)); Log($"release {args[i]}"); break;
            // A wheel turn of N (120 a notch; positive is away from you, or right for hwheel).
            case "wheel": Wheel(MOUSEEVENTF_WHEEL, int.Parse(args[++i])); break;
            case "hwheel": Wheel(0x1000, int.Parse(args[++i])); break; // MOUSEEVENTF_HWHEEL
            case "wait": Thread.Sleep(int.Parse(args[++i])); break;
            case "cursor": Log($"cursor {CursorName()}"); break;
            default: throw new ArgumentException($"unknown step {args[i]}");
        }
    }
    return 0;
}
catch (Exception e)
{
    Log($"failed: {e}");
    return 1;
}

void Shot(string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    using var bmp = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format32bppRgb);
    using (var g = Graphics.FromImage(bmp))
    {
        nint dst = g.GetHdc();
        nint screen = GetDC(0);
        // CAPTUREBLT includes layered windows (our UI layer sits in one).
        Gdi32.BitBlt(dst, 0, 0, monitor.Width, monitor.Height, screen, monitor.Left, monitor.Top, Gdi32.SRCCOPY | Gdi32.CAPTUREBLT);
        ReleaseDC(0, screen);
        g.ReleaseHdc(dst);
    }
    bmp.Save(path, ImageFormat.Png);
    Log($"shot {path} {monitor.Width}x{monitor.Height}");
}

void Move(int x, int y)
{
    var input = new INPUT { type = INPUT_MOUSE };
    input.u.mi.dx = (int)((x - monitor.Left) * 65535.0 / (monitor.Width - 1));
    input.u.mi.dy = (int)((y - monitor.Top) * 65535.0 / (monitor.Height - 1));
    input.u.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
    Send(input);
}

void Mouse(uint flags)
{
    var input = new INPUT { type = INPUT_MOUSE };
    input.u.mi.dwFlags = flags;
    Send(input);
}

void Wheel(uint flags, int delta)
{
    var input = new INPUT { type = INPUT_MOUSE };
    input.u.mi.dwFlags = flags;
    input.u.mi.mouseData = delta;
    Send(input);
    Log($"{(flags == MOUSEEVENTF_WHEEL ? "wheel" : "hwheel")} {delta}");
}

void Key(string combo)
{
    var codes = combo.ToLowerInvariant().Split('+').Select(ScanCode).ToList();
    foreach (var c in codes) Send(KeyInput(c, up: false));
    Thread.Sleep(40);
    foreach (var c in Enumerable.Reverse(codes)) Send(KeyInput(c, up: true));
    Log($"key {combo}");
}

static INPUT KeyInput(ushort scan, bool up)
{
    var input = new INPUT { type = INPUT_KEYBOARD };
    input.u.ki.wScan = (ushort)(scan & 0xFF);
    input.u.ki.dwFlags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0) | ((scan & 0xE000) == 0xE000 ? KEYEVENTF_EXTENDEDKEY : 0);
    return input;
}

static ushort ScanCode(string name) => name switch
{
    "ctrl" => 0x1D, "alt" => 0x38, "shift" => 0x2A, "win" => 0xE05B,
    "esc" => 0x01, "grave" or "`" => 0x29, "tab" => 0x0F, "enter" => 0x1C, "space" => 0x39,
    "left" => 0xE04B, "right" => 0xE04D, "up" => 0xE048, "down" => 0xE050,
    "0" => 0x0B, "1" => 0x02, "minus" => 0x0C, "equals" => 0x0D,
    // Letters by their place on the keyboard (scan codes don't follow the alphabet).
    [var c] when "qwertyuiop".IndexOf(c) is var i and >= 0 => (ushort)(0x10 + i),
    [var c] when "asdfghjkl".IndexOf(c) is var i and >= 0 => (ushort)(0x1E + i),
    [var c] when "zxcvbnm".IndexOf(c) is var i and >= 0 => (ushort)(0x2C + i),
    _ => throw new ArgumentException($"unknown key {name}"),
};

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE devMode);

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int ChangeDisplaySettingsEx(string? device, ref DEVMODE devMode, nint hwnd, uint flags, nint param);

// Which standard cursor is showing (screenshots don't include it).
static string CursorName()
{
    var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
    if (!GetCursorInfo(ref info)) return "unknown";
    if ((info.flags & 1) == 0) return "hidden";
    foreach (var (id, name) in new[] { (32512, "arrow"), (32646, "sizeall"), (32649, "hand"), (32513, "ibeam"), (32514, "wait") })
        if (info.hCursor == LoadCursor(0, id)) return name;
    return $"0x{info.hCursor:X}";
}

[DllImport("user32.dll")]
static extern bool GetCursorInfo(ref CURSORINFO info);

static void Send(INPUT input)
{
    if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) != 1)
        throw new InvalidOperationException($"SendInput failed ({Marshal.GetLastPInvokeError()})");
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct DEVMODE
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
    public int dmFields;
    public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    public short dmLogPixels;
    public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
}

[StructLayout(LayoutKind.Sequential)]
struct CURSORINFO { public int cbSize, flags; public nint hCursor; public POINT ptScreenPos; }
