using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TailPad.Host;

/// <summary>Draws the tray/window icon at runtime so the repo needs no binary assets.</summary>
static class AppIcon
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    public static readonly Color Accent = Color.FromArgb(0x39, 0xFF, 0x88);

    public static Icon Make(bool connected, int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 32f;
            using var body = new GraphicsPath();
            // gamepad silhouette
            body.AddArc(2 * s, 9 * s, 12 * s, 16 * s, 90, 180);
            body.AddLine(8 * s, 9 * s, 24 * s, 9 * s);
            body.AddArc(18 * s, 9 * s, 12 * s, 16 * s, 270, 180);
            body.AddLine(24 * s, 25 * s, 8 * s, 25 * s);
            body.CloseFigure();
            using var fill = new SolidBrush(connected ? Accent : Color.FromArgb(0xB0, 0xB6, 0xC4));
            g.FillPath(fill, body);
            using var dark = new SolidBrush(Color.FromArgb(0x10, 0x14, 0x1A));
            g.FillRectangle(dark, 6.5f * s, 15.5f * s, 6 * s, 2 * s);
            g.FillRectangle(dark, 8.5f * s, 13.5f * s, 2 * s, 6 * s);
            g.FillEllipse(dark, 21 * s, 13 * s, 3 * s, 3 * s);
            g.FillEllipse(dark, 24 * s, 16.5f * s, 3 * s, 3 * s);
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }
}
