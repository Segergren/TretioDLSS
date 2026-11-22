using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TretioDLSS.Platform;

/// <summary>Window icons: .ico files built from one picture, and icon handles loaded from them.</summary>
public static class IconFile
{
    /// <summary>Title bar, taskbar and Alt+Tab sizes from 100 to 200 percent scaling.</summary>
    public static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];

    /// <summary>An .ico with the picture scaled to every size in <see cref="Sizes"/>, each frame stored as PNG.</summary>
    public static byte[] FromImage(byte[] image)
    {
        using var source = new Bitmap(new MemoryStream(image));
        var frames = Sizes.Select(size => (size, data: Scale(source, size))).ToList();
        using var ico = new MemoryStream();
        using var w = new BinaryWriter(ico);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var (size, data) in frames)
        {
            // A size byte of 0 means 256.
            w.Write((byte)(size % 256));
            w.Write((byte)(size % 256));
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in frames) w.Write(data);
        w.Flush();
        return ico.ToArray();
    }

    private static byte[] Scale(Bitmap source, int size)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        using (var edges = new ImageAttributes())
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            edges.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, edges);
        }
        using var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    /// <summary>An icon handle of this size from an .ico file, or zero. Free it with <see cref="Destroy"/>.</summary>
    public static IntPtr Load(string path, int size) => LoadImage(IntPtr.Zero, path, ImageIcon, size, size, LoadFromFile);

    public static void Destroy(IntPtr icon)
    {
        if (icon != IntPtr.Zero) DestroyIcon(icon);
    }

    private const uint ImageIcon = 1, LoadFromFile = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
