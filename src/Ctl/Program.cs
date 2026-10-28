using System.IO.Pipes;

// expansectl: sends one command to the running Expanse and prints the reply.
//   expansectl state | log [N] | toggle | zoomout | back | pick <title> | view X Y SCALE | bench [S] | quit
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: expansectl <command> [args]   (try: expansectl help)");
    return 2;
}
string pipeName = "expanse-" + Environment.UserName.ToLowerInvariant();
using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
try
{
    pipe.Connect(3000);
}
catch (TimeoutException)
{
    Console.Error.WriteLine("Expanse isn't running (no " + pipeName + " pipe)");
    return 1;
}
var writer = new StreamWriter(pipe) { AutoFlush = true };
writer.WriteLine(string.Join(' ', args));
Console.Write(new StreamReader(pipe).ReadToEnd());
return 0;
