using System.IO.Pipes;
using System.Runtime.InteropServices;
using Expanse.Native;

namespace Expanse;

/// A command from expansectl, answered on the UI thread.
sealed class Request(string text)
{
    readonly TaskCompletionSource<string> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Text { get; } = text;
    public Task<string> Completion => done.Task;
    public void Reply(string text) => done.TrySetResult(text);
}

/// `\\.\pipe\expanse-<user>`: one line in, the reply out. The pipe is reachable from an SSH
/// session as well as the desktop, which is how the Mac drives Expanse in the VM.
sealed class CommandServer(nint overlay)
{
    public static string PipeName => "expanse-" + Environment.UserName.ToLowerInvariant();

    public void Start() => new Thread(Loop) { IsBackground = true, Name = "commands" }.Start();

    void Loop()
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte);
                pipe.WaitForConnection();
                var reader = new StreamReader(pipe);
                var request = new Request(reader.ReadLine() ?? "");
                var handle = GCHandle.Alloc(request);
                if (!User32.PostMessage(overlay, Overlay.WM_COMMAND_REQUEST, GCHandle.ToIntPtr(handle), 0))
                {
                    handle.Free();
                    request.Reply("couldn't reach the UI thread");
                }
                string reply = request.Completion.Wait(TimeSpan.FromSeconds(60)) ? request.Completion.Result : "timed out";
                var writer = new StreamWriter(pipe) { AutoFlush = true };
                writer.Write(reply.EndsWith('\n') ? reply : reply + "\n");
                pipe.WaitForPipeDrain();
            }
            catch (Exception e)
            {
                Log.Info($"commands: {e.Message}");
                Thread.Sleep(200);
            }
        }
    }
}
