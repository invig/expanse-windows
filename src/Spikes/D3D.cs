using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace Spikes;

static class D3D
{
    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, int[]? levels,
        uint levelCount, uint sdkVersion, out nint device, out int level, out nint context);

    [DllImport("d3d11.dll")]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    const int Hardware = 1, Warp = 5;
    const uint BgraSupport = 0x20;

    public static string Driver { get; private set; } = "";

    /// A D3D11 device: the GPU if there is one, else WARP (software), which is what a VM
    /// without a 3D driver has.
    public static ID3D11Device Create()
    {
        foreach (var (type, name) in new[] { (Hardware, "hardware"), (Warp, "WARP") })
        {
            if (D3D11CreateDevice(0, type, 0, BgraSupport, null, 0, 7, out nint device, out int level, out nint context) != 0)
                continue;
            Marshal.Release(context);
            Driver = $"{name}, feature level 0x{level:X}";
            return new ID3D11Device(device);
        }
        throw new InvalidOperationException("no D3D11 device");
    }
}
