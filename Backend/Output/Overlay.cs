using TretioDLSS.Graphics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TretioDLSS.Output;

/// <summary>Stats overlay drawn with Direct2D onto the back buffer before present.</summary>
public sealed class Overlay : IDisposable
{
    private readonly ID2D1Factory1 _factory;
    private readonly IDWriteFactory _dwrite;
    private readonly ID2D1Device _device;
    private readonly ID2D1DeviceContext _ctx;
    private readonly IDWriteTextFormat _text;
    private readonly IDWriteTextFormat _label;
    private ID2D1Bitmap1? _target;
    private ID2D1SolidColorBrush? _panel, _chalk, _dusk;
    private volatile string[] _lines = [];

    public bool Visible { get; set; }
    public bool Attached => _target != null;

    public Overlay(GraphicsDevice g)
    {
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        using var dxgi = g.Device.QueryInterface<IDXGIDevice>();
        _device = _factory.CreateDevice(dxgi);
        _ctx = _device.CreateDeviceContext(DeviceContextOptions.None);
        _text = _dwrite.CreateTextFormat("Bahnschrift", null, FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.SemiCondensed, 20, "en-us");
        _label = _dwrite.CreateTextFormat("Bahnschrift", null, FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, FontStretch.SemiCondensed, 14, "en-us");
    }

    public void SetLines(string[] lines) => _lines = lines;

    /// <param name="white">Overlay white in target units: 1 for SDR, paper white / 80 nits for scRGB.</param>
    public void Attach(Vortice.Direct3D11.ID3D11Texture2D back, Format format = Format.R8G8B8A8_UNorm, float white = 1f)
    {
        Detach();
        using var surface = back.QueryInterface<IDXGISurface>();
        var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(format, Vortice.DCommon.AlphaMode.Ignore), 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw);
        _target = _ctx.CreateBitmapFromDxgiSurface(surface, props);
        _ctx.Target = _target;
        // scRGB brushes are linear: sRGB colors are decoded and scaled to paper white.
        Color4 C(float r, float g, float b, float a) => white == 1f ? new Color4(r, g, b, a) : new Color4(Lin(r) * white, Lin(g) * white, Lin(b) * white, a);
        _panel?.Dispose(); _chalk?.Dispose(); _dusk?.Dispose();
        _panel = _ctx.CreateSolidColorBrush(C(0.071f, 0.082f, 0.102f, 0.82f));
        _chalk = _ctx.CreateSolidColorBrush(C(1, 1, 1, 1));
        _dusk = _ctx.CreateSolidColorBrush(C(0.573f, 0.604f, 0.659f, 1));
    }

    private static float Lin(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public void Detach()
    {
        _ctx.Target = null;
        _target?.Dispose();
        _target = null;
    }

    public void Draw()
    {
        if (!Visible || _target == null) return;
        var lines = _lines;
        if (lines.Length == 0) return;
        _ctx.BeginDraw();
        float x = 24, y = 24, w = 300, lineH = 26;
        float h = 20 + lines.Length * lineH;
        _ctx.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), 12, 12), _panel!);
        for (int i = 0; i < lines.Length; i++)
        {
            var parts = lines[i].Split('\t');
            var row = new System.Drawing.RectangleF(x + 16, y + 10 + i * lineH, w - 32, lineH);
            _ctx.DrawText(parts[0], _label, new Rect(row.X, row.Y, row.Width, row.Height), _dusk!);
            if (parts.Length > 1)
            {
                using var layout = _dwrite.CreateTextLayout(parts[1], _text, w - 32, lineH);
                layout.TextAlignment = TextAlignment.Trailing;
                _ctx.DrawTextLayout(new System.Numerics.Vector2(row.Left, row.Top - 3), layout, _chalk!);
            }
        }
        _ctx.EndDraw();
    }

    public void Dispose()
    {
        Detach();
        _panel?.Dispose();
        _chalk?.Dispose();
        _dusk?.Dispose();
        _text.Dispose();
        _label.Dispose();
        _ctx.Dispose();
        _device.Dispose();
        _dwrite.Dispose();
        _factory.Dispose();
    }
}
