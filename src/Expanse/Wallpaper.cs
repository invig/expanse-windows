using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using Expanse.Native;

namespace Expanse;

/// The desktop picture, filled to the screen, as a GDI bitmap ready to blit behind the canvas.
sealed class Wallpaper : IDisposable
{
    nint dc, bitmap, oldBitmap;
    int width, height;
    string? path;

    public void Draw(nint hdc, int w, int h, bool enabled)
    {
        if (enabled)
        {
            string? current = CurrentPath();
            if (dc == 0 || w != width || h != height || current != path) Load(current, w, h);
        }
        if (enabled && dc != 0) Gdi32.BitBlt(hdc, 0, 0, w, h, dc, 0, 0, Gdi32.SRCCOPY);
        else
        {
            var r = new RECT(0, 0, w, h);
            nint brush = Gdi32.CreateSolidBrush(Gdi32.RGB(28, 28, 30));
            User32.FillRect(hdc, ref r, brush);
            Gdi32.DeleteObject(brush);
        }
    }

    static string? CurrentPath()
    {
        var sb = new StringBuilder(1024);
        return User32.SystemParametersInfo(User32.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) && sb.Length > 0
            ? sb.ToString() : null;
    }

    void Load(string? file, int w, int h)
    {
        Dispose();
        path = file;
        width = w;
        height = h;
        if (file == null || !File.Exists(file)) return;
        try
        {
            using var image = Image.FromFile(file);
            using var canvas = new Bitmap(w, h);
            using (var g = Graphics.FromImage(canvas))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                double s = Math.Max((double)w / image.Width, (double)h / image.Height); // fill, like "Fill" in Settings
                int dw = (int)Math.Ceiling(image.Width * s), dh = (int)Math.Ceiling(image.Height * s);
                g.DrawImage(image, (w - dw) / 2, (h - dh) / 2, dw, dh);
            }
            bitmap = canvas.GetHbitmap();
            dc = Gdi32.CreateCompatibleDC(0);
            oldBitmap = Gdi32.SelectObject(dc, bitmap);
        }
        catch (Exception e)
        {
            Log.Info($"wallpaper: couldn't load {file}: {e.Message}");
        }
    }

    public void Dispose()
    {
        if (dc != 0) { Gdi32.SelectObject(dc, oldBitmap); Gdi32.DeleteDC(dc); dc = 0; }
        if (bitmap != 0) { Gdi32.DeleteObject(bitmap); bitmap = 0; }
    }
}
