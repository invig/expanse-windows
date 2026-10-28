using Expanse;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Spikes;

/// SetForegroundWindow from a process that has had no input (started by Task Scheduler): the
/// case of Expanse acting on its own, e.g. a command over the pipe or a timer. The hotkey and
/// overlay-click cases are logged by Expanse itself on every zoom-out and dive.
static class Foreground
{
    public static int Run()
    {
        var found = CanvasWindow.Scan();
        if (found.Count < 2) throw new InvalidOperationException("need two windows");
        nint before = GetForegroundWindow();
        nint target = found[^1]; // the bottom-most
        var steps = new List<string>();
        bool direct = SetForegroundWindow(target);
        steps.Add($"direct: returned {direct}, foreground is target: {GetForegroundWindow() == target}");
        if (GetForegroundWindow() != target)
        {
            var inputs = new INPUT[2];
            inputs[0].type = inputs[1].type = INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = inputs[1].u.ki.wVk = VK_MENU;
            inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
            SendInput(2, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
            bool alt = SetForegroundWindow(target);
            steps.Add($"after Alt tap: returned {alt}, foreground is target: {GetForegroundWindow() == target}");
        }
        if (GetForegroundWindow() != target)
        {
            uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint me = Kernel32.GetCurrentThreadId();
            bool attached = AttachThreadInput(me, fg, true);
            BringWindowToTop(target);
            bool attachedResult = SetForegroundWindow(target);
            if (attached) AttachThreadInput(me, fg, false);
            steps.Add($"AttachThreadInput (attached {attached}): returned {attachedResult}, foreground is target: {GetForegroundWindow() == target}");
        }
        Out.Result("foreground", new
        {
            before = $"{User32.Title(before)} [0x{before:X}]",
            target = $"{User32.Title(target)} [0x{target:X}]",
            steps,
        });
        if (IsWindow(before)) SetForegroundWindow(before);
        return 0;
    }
}
