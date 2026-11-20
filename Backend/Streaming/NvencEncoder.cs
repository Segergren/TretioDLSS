using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Serilog;
using TretioDLSS.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TretioDLSS.Streaming;

public sealed record EncodedFrame(byte[] Data, bool Idr, long FrameIndex, long CaptureQpc, long SubmitQpc);

/// <summary>
/// NVENC on the pipeline's device. The pipeline thread converts to NV12 and submits; a worker thread
/// waits for each frame and hands the bitstream on, so encoding never blocks presenting.
/// </summary>
public sealed unsafe class NvencEncoder : IDisposable
{
    public const int Ring = 4;
    private readonly GraphicsDevice _g;
    private IntPtr _enc;
    private readonly ID3D11Texture2D[] _textures = new ID3D11Texture2D[Ring];
    private readonly ID3D11RenderTargetView[] _luma = new ID3D11RenderTargetView[Ring];
    private readonly ID3D11RenderTargetView[] _chroma = new ID3D11RenderTargetView[Ring];
    private readonly BlockingCollection<(int Slot, long Index, long Capture, long Submit)> _pending = new(Ring);
    private readonly Thread _worker;
    private readonly Action<EncodedFrame> _output;
    private int _next;
    private long _frameIndex;
    private volatile bool _forceIdr = true;

    public int Width { get; }
    public int Height { get; }
    public string Codec { get; }
    public long Dropped;

    public NvencEncoder(GraphicsDevice g, int codec, int width, int height, int fps, int kbps, Action<EncodedFrame> output)
    {
        _g = g;
        _output = output;
        Width = width & ~1;
        Height = height & ~1;
        Codec = codec switch { 2 => "AV1", 1 => "HEVC", _ => "H.264" };
        var err = stackalloc byte[512];
        _enc = StreamNative.ts_nvenc_create(g.Device.NativePointer, codec, Width, Height, fps, kbps, Ring, err, 512);
        if (_enc == IntPtr.Zero) throw new InvalidOperationException("NVENC: " + StreamNative.Text(err));
        for (int i = 0; i < Ring; i++)
        {
            var ptr = StreamNative.ts_nvenc_texture(_enc, i);
            Marshal.AddRef(ptr);
            _textures[i] = new ID3D11Texture2D(ptr);
            _luma[i] = g.Device.CreateRenderTargetView(_textures[i], new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, Format.R8_UNorm));
            _chroma[i] = g.Device.CreateRenderTargetView(_textures[i], new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, Format.R8G8_UNorm));
        }
        _worker = new Thread(Collect) { IsBackground = true, Name = "NvencCollect", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
        Log.Information("NVENC {Codec} {W}x{H}@{Fps}, {Kbps} kbps CBR, P1 ultra low latency", Codec, Width, Height, fps, kbps);
    }

    public void RequestIdr() => _forceIdr = true;

    /// <summary>Pipeline thread: converts the RGBA frame to NV12 and submits it. False when every slot is still busy.</summary>
    public bool Submit(RenderTexture rgba, long captureQpc)
    {
        int slot = _next;
        if (StreamNative.ts_nvenc_slot_busy(_enc, slot) != 0)
        {
            Interlocked.Increment(ref Dropped);
            return false;
        }
        _g.Draw(_luma[slot], _g.PixelShader("encode.hlsl", "PSLuma"), new Viewport(0, 0, Width, Height), [rgba.Srv]);
        _g.Draw(_chroma[slot], _g.PixelShader("encode.hlsl", "PSChroma"), new Viewport(0, 0, Width / 2, Height / 2), [rgba.Srv]);
        bool idr = _forceIdr;
        long index = ++_frameIndex;
        int r = StreamNative.ts_nvenc_submit(_enc, slot, idr ? 1 : 0, (ulong)index);
        if (r != 0)
        {
            Log.Warning("NVENC submit failed ({R}) for frame {Index}", r, index);
            _frameIndex--;
            return false;
        }
        if (idr) _forceIdr = false;
        _next = (slot + 1) % Ring;
        _pending.Add((slot, index, captureQpc, Pipeline.Qpc.Now));
        return true;
    }

    private void Collect()
    {
        foreach (var (slot, index, capture, submit) in _pending.GetConsumingEnumerable())
        {
            int r = StreamNative.ts_nvenc_collect(_enc, slot, 2000, out byte* data, out int len, out int idr);
            if (r != 0)
            {
                Log.Warning("NVENC collect failed ({R}) for frame {Index}", r, index);
                StreamNative.ts_nvenc_release(_enc, slot);
                _forceIdr = true;
                continue;
            }
            var bytes = new ReadOnlySpan<byte>(data, len).ToArray();
            StreamNative.ts_nvenc_release(_enc, slot);
            try { _output(new EncodedFrame(bytes, idr != 0, index, capture, submit)); }
            catch (Exception ex) { Log.Warning(ex, "Sending encoded frame {Index} failed", index); }
        }
    }

    public void Dispose()
    {
        _pending.CompleteAdding();
        _worker.Join(3000);
        for (int i = 0; i < Ring; i++)
        {
            _luma[i]?.Dispose();
            _chroma[i]?.Dispose();
            _textures[i]?.Dispose();
        }
        if (_enc != IntPtr.Zero) StreamNative.ts_nvenc_destroy(_enc);
        _enc = IntPtr.Zero;
    }
}
