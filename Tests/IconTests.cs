using System.Drawing;
using System.Drawing.Imaging;
using TretioDLSS.Platform;

namespace TretioDLSS.Tests;

public class IconTests
{
    [Fact]
    public void GameIconHasEverySizeAndLoads()
    {
        var color = Color.FromArgb(255, 200, 40, 90);
        byte[] png;
        using (var source = new Bitmap(256, 256))
        {
            using (var g = System.Drawing.Graphics.FromImage(source)) g.Clear(color);
            using var ms = new MemoryStream();
            source.Save(ms, ImageFormat.Png);
            png = ms.ToArray();
        }

        byte[] ico = IconFile.FromImage(png);
        int count = BitConverter.ToUInt16(ico, 4);
        Assert.Equal(IconFile.Sizes.Select(s => s % 256), Enumerable.Range(0, count).Select(i => (int)ico[6 + 16 * i]));

        string path = Path.Combine(Path.GetTempPath(), "tretio-tests", Guid.NewGuid() + ".ico");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ico);
        foreach (int size in new[] { 16, 24, 32, 48 })
        {
            IntPtr h = IconFile.Load(path, size);
            Assert.NotEqual(IntPtr.Zero, h);
            try
            {
                using var icon = Icon.FromHandle(h);
                using var bmp = icon.ToBitmap();
                Assert.Equal(size, bmp.Width);
                Assert.Equal(color.ToArgb(), bmp.GetPixel(size / 2, size / 2).ToArgb());
            }
            finally
            {
                IconFile.Destroy(h);
            }
        }
    }
}
