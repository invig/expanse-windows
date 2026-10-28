using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// System-wide window events: shown, hidden, destroyed, moved, renamed, minimised, cloaked and
/// focus. Out of context, so nothing is injected into other processes (an ARM64 Expanse couldn't
/// inject into x64 apps anyway). Windows queues them to the thread that installed the hook, so
/// they arrive on the UI thread between messages, like any other input.
sealed class WinEvents : IDisposable
{
    readonly WinEventProc proc; // kept alive: Windows holds a pointer to it
    readonly List<nint> hooks = new();
    readonly Action<uint, nint> handler;

    public WinEvents(Action<uint, nint> handler)
    {
        this.handler = handler;
        proc = Callback;
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE);
        Hook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_NAMECHANGE);
        Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);
        Log.Info($"window events: {hooks.Count(h => h != 0)} of {hooks.Count} hooks");
    }

    void Hook(uint min, uint max) =>
        hooks.Add(SetWinEventHook(min, max, 0, proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));

    /// Only whole windows: not their carets, scroll bars or other parts.
    void Callback(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == 0 || idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
        try { handler(ev, hwnd); }
        catch (Exception e) { Log.Info($"window events: handler threw {e}"); }
    }

    public void Dispose()
    {
        foreach (var h in hooks) if (h != 0) UnhookWinEvent(h);
        hooks.Clear();
    }
}
