using Spikes;

// Week-one experiments, run on the VM's desktop (scripts/vm.sh desk ... Spikes.exe MODE).
// Each writes a log and a JSON result to %USERPROFILE%\expanse-windows\out.
//   sources N                 N GDI windows painting a counter at ~30 fps, spread over a big canvas
//   pictures dwm|shared S     every window as a live picture, camera swinging for S seconds
//   wgc S                     Windows.Graphics.Capture on every window for S seconds
//   parking [S]               which apps keep painting when parked off-screen (WGC frame counts)
//   foreground                SetForegroundWindow from a process that got no input
try
{
    return args.FirstOrDefault() switch
    {
        "sources" => Sources.Run(int.Parse(args[1])),
        "pictures" => PictureBench.Run(args[1], double.Parse(args[2])),
        "wgc" => Capture.RunBench(double.Parse(args[1])),
        "parking" => Capture.RunParking(args.Length > 1 ? double.Parse(args[1]) : 3),
        "foreground" => Foreground.Run(),
        _ => throw new ArgumentException("modes: sources N | pictures dwm|shared S | wgc S | parking [S] | foreground"),
    };
}
catch (Exception e)
{
    Out.Log($"{string.Join(' ', args)} failed: {e}");
    return 1;
}
