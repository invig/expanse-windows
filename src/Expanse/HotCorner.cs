using System.Diagnostics;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// Rest the pointer in a corner of the main screen to zoom out (or back), as on the Mac: polled
/// (cheap, and it works whichever app is in front), fired after a moment there with no button
/// held, and armed again once the pointer leaves the corner.
///
/// Every corner to begin with, as on the Mac. On Windows the top-right is also where you fling
/// the pointer to close a maximised window, and the bottom-right is Show Desktop; the dwell (and
/// no button held) keeps a quick fling or click from zooming out.
sealed class HotCorner
{
    public static readonly (string key, string title)[] Choices =
    {
        ("off", "Off"), ("all", "All Corners"), ("topLeft", "Top Left"), ("topRight", "Top Right"),
        ("bottomLeft", "Bottom Left"), ("bottomRight", "Bottom Right"),
    };
    public const string Default = "all";

    const double Dwell = 0.06;   // seconds in the corner
    const int Tolerance = 3;     // pixels from the edge that count as the corner
    long? enteredAt;
    bool armed = true;

    /// Waits for the pointer to leave the corner before it can fire again: while DWM restarts the
    /// pointer reads as being in the top-left corner.
    public void Disarm()
    {
        armed = false;
        enteredAt = null;
    }

    /// One poll: true when the pointer has just rested in `corner` of `screen` long enough.
    public bool Poll(string corner, RECT screen)
    {
        if (corner == "off" || !GetCursorPos(out var p)) return false;
        bool left = p.X <= screen.Left + Tolerance, right = p.X >= screen.Right - 1 - Tolerance;
        bool top = p.Y <= screen.Top + Tolerance, bottom = p.Y >= screen.Bottom - 1 - Tolerance;
        bool inCorner = corner switch
        {
            "all" => (top || bottom) && (left || right),
            "topLeft" => top && left,
            "topRight" => top && right,
            "bottomLeft" => bottom && left,
            "bottomRight" => bottom && right,
            _ => false,
        };
        bool buttons = GetAsyncKeyState(VK_LBUTTON) < 0 || GetAsyncKeyState(VK_RBUTTON) < 0 || GetAsyncKeyState(VK_MBUTTON) < 0;
        if (!inCorner || buttons)
        {
            enteredAt = null;
            armed = true;
            return false;
        }
        if (!armed) return false;
        long now = Stopwatch.GetTimestamp();
        if (enteredAt is not long since) { enteredAt = now; return false; }
        if (Stopwatch.GetElapsedTime(since, now).TotalSeconds < Dwell) return false;
        armed = false;
        enteredAt = null;
        return true;
    }
}
