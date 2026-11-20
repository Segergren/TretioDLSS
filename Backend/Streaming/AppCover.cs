using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace TretioDLSS.Streaming;

/// <summary>The box art Moonlight shows for the TretioDLSS app: the app mark on the app's dark background, 600 x 800.</summary>
public static class AppCover
{
    private static readonly Lazy<byte[]> Png = new(Render);

    public static byte[] Bytes => Png.Value;

    private static byte[] Render()
    {
        const int w = 600, h = 800;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(0x15, 0x19, 0x1f), Color.FromArgb(0x0e, 0x10, 0x14), 90f))
            g.FillRectangle(bg, 0, 0, w, h);

        // The two frames of the app icon, the coral one lying on top.
        float s = 1.9f, ox = (w - 182 * s) / 2 - 37 * s, oy = 250 - 67 * s;
        RectangleF Frame(float x, float y) => new(ox + x * s, oy + y * s, 120 * s, 86 * s);
        using (var gray = new Pen(Color.FromArgb(0x4d, 0x55, 0x65), 14 * s))
            DrawRounded(g, gray, Frame(44, 74), 14 * s);
        using (var coral = new Pen(Color.FromArgb(0xff, 0x5a, 0x5f), 14 * s))
            DrawRounded(g, coral, Frame(92, 104), 14 * s);

        using var title = new Font(Family("IBM Plex Sans Condensed", "Segoe UI"), 58, FontStyle.Bold, GraphicsUnit.Pixel);
        var center = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString("TretioDLSS", title, Brushes.White, new RectangleF(0, 580, w, 80), center);

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static FontFamily Family(string preferred, string fallback)
    {
        using var installed = new InstalledFontCollection();
        return installed.Families.FirstOrDefault(f => f.Name == preferred) ?? new FontFamily(fallback);
    }

    private static void DrawRounded(System.Drawing.Graphics g, Pen pen, RectangleF r, float radius)
    {
        using var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }
}
