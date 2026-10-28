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
            case "shot": Shot(args[++i], monitor); break;
            case "shotall": // every display: the whole virtual screen
                Shot(args[++i], new RECT(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
                    GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN),
                    GetSystemMetrics(SM_YVIRTUALSCREEN) + GetSystemMetrics(SM_CYVIRTUALSCREEN)));
                break;
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
            case "res": // res W H [N]: display N (\\.\DISPLAYn), the main one if left out
            {
                int w = int.Parse(args[++i]), h = int.Parse(args[++i]);
                string? device = i + 1 < args.Length && int.TryParse(args[i + 1], out int n) ? $"\\\\.\\DISPLAY{n}" : null;
                if (device != null) i++;
                var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>(), dmDeviceName = "", dmFormName = "" };
                EnumDisplaySettings(device, -1, ref mode); // ENUM_CURRENT_SETTINGS
                mode.dmPelsWidth = w;
                mode.dmPelsHeight = h;
                // Its position kept, stored, then applied for every display at once: changed on its
                // own, a display's mode takes the others out of the desktop.
                mode.dmFields = 0x80000 | 0x100000 | 0x20; // DM_PELSWIDTH | DM_PELSHEIGHT | DM_POSITION
                int result = ChangeDisplaySettingsEx(device, ref mode, 0, 0x10000000 | 0x1, 0); // CDS_NORESET | CDS_UPDATEREGISTRY
                if (result == 0) result = ApplyDisplaySettings(null, 0, 0, 0, 0);
                Log($"res {device ?? "main"} {w}x{h}: {result}");
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
            case "scale": // scale P [N]: display N's scaling in percent (the first one if left out)
            {
                int percent = int.Parse(args[++i]);
                string? device = i + 1 < args.Length && int.TryParse(args[i + 1], out int n) ? $"\\\\.\\DISPLAY{n}" : null;
                if (device != null) i++;
                SetScale(percent, device);
                break;
            }
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

void Shot(string path, RECT area)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    using var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
    using (var g = Graphics.FromImage(bmp))
    {
        nint dst = g.GetHdc();
        nint screen = GetDC(0);
        // CAPTUREBLT includes layered windows (our UI layer sits in one).
        Gdi32.BitBlt(dst, 0, 0, area.Width, area.Height, screen, area.Left, area.Top, Gdi32.SRCCOPY | Gdi32.CAPTUREBLT);
        ReleaseDC(0, screen);
        g.ReleaseHdc(dst);
    }
    bmp.Save(path, ImageFormat.Png);
    Log($"shot {path} {area.Width}x{area.Height} at {area.Left},{area.Top}");
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
    "0" => 0x0B, "1" => 0x02, "minus" => 0x0C, "equals" => 0x0D, "f11" => 0x57,
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
[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
static extern int ApplyDisplaySettings(string? device, nint devMode, nint hwnd, uint flags, nint param);

// The main display's scaling (Settings > Display > Scale), through the undocumented
// DisplayConfig device-info types -3/-4 that Settings uses: the scale is set as steps from the
// display's recommended one.
void SetScale(int percent, string? device)
{
    int[] steps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };
    int want = Array.IndexOf(steps, percent);
    if (want < 0) throw new ArgumentException($"no {percent}% scale");
    GetDisplayConfigBufferSizes(2 /* QDC_ONLY_ACTIVE_PATHS */, out uint paths, out uint modes);
    var pathInfo = new byte[paths * 72];
    var modeInfo = new byte[modes * 64];
    int qr = QueryDisplayConfig(2, ref paths, pathInfo, ref modes, modeInfo, 0);
    if (qr != 0 || paths == 0) throw new InvalidOperationException($"QueryDisplayConfig returned {qr}");
    // The display's path: each path (72 bytes) starts with its source's adapter LUID and id; the
    // source's GDI name (\\.\DISPLAYn) picks the one asked for.
    long adapter = 0;
    uint source = 0;
    bool found = false;
    for (int p = 0; p < paths && !found; p++)
    {
        adapter = BitConverter.ToInt64(pathInfo, p * 72);
        source = BitConverter.ToUInt32(pathInfo, p * 72 + 8);
        var name = new SourceName { type = 1, size = (uint)Marshal.SizeOf<SourceName>(), adapter = adapter, id = source, gdiName = "" };
        found = device == null || (DisplayConfigGetSourceName(ref name) == 0 && string.Equals(name.gdiName, device, StringComparison.OrdinalIgnoreCase));
    }
    if (!found) throw new ArgumentException($"no display {device}");
    var get = new DpiScaleGet { type = -3, size = (uint)Marshal.SizeOf<DpiScaleGet>(), adapter = adapter, id = source };
    if (DisplayConfigGetDeviceInfo(ref get) != 0) throw new InvalidOperationException("couldn't read the scale");
    int recommended = Math.Abs(get.minRel);
    var set = new DpiScaleSet { type = -4, size = (uint)Marshal.SizeOf<DpiScaleSet>(), adapter = adapter, id = source, rel = want - recommended };
    int sr = DisplayConfigSetDeviceInfo(ref set);
    Log($"scale {steps[recommended + get.curRel]}% -> {percent}% (recommended {steps[recommended]}%): {sr}");
    if (sr != 0) throw new InvalidOperationException($"DisplayConfigSetDeviceInfo returned {sr}");
}

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

[DllImport("user32.dll")]
static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
[DllImport("user32.dll")]
static extern int QueryDisplayConfig(uint flags, ref uint paths, byte[] pathInfo, ref uint modes, byte[] modeInfo, nint topology);
[DllImport("user32.dll")]
static extern int DisplayConfigGetDeviceInfo(ref DpiScaleGet info);
[DllImport("user32.dll")]
static extern int DisplayConfigSetDeviceInfo(ref DpiScaleSet info);
[DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
static extern int DisplayConfigGetSourceName(ref SourceName info);

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

// DISPLAYCONFIG_DEVICE_INFO_HEADER, then the scale (relative to the recommended one).
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct DpiScaleGet { public int type; public uint size; public long adapter; public uint id; public int minRel, curRel, maxRel; }

[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct DpiScaleSet { public int type; public uint size; public long adapter; public uint id; public int rel; }

// DISPLAYCONFIG_SOURCE_DEVICE_NAME: the header, then the GDI name.
[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
struct SourceName { public int type; public uint size; public long adapter; public uint id; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string gdiName; }
