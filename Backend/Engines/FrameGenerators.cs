using System.Runtime.InteropServices;
using TretioDLSS.Graphics;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace TretioDLSS.Engines;

public static class FrameGen
{
    /// <summary>Highest multiplier an engine supports.</summary>
    public static int MaxMultiplier(string engine) => engine switch
    {
        "vfx-vfg" or "blend" => 8,
        "amd-frc" => 2,
        _ => 4,
    };

    public static int[] Multipliers(string engine) => Enumerable.Range(2, MaxMultiplier(engine) - 1).ToArray();

    public static int ClampMultiplier(string engine, int multiplier) => Math.Clamp(multiplier, 2, MaxMultiplier(engine));
}

public interface IFrameGenerator : IDisposable
{
    string Id { get; }

    /// <summary>Writes the frame at position t (0..1) between previous and current into output.</summary>
    void Generate(RenderTexture previous, RenderTexture current, float t, RenderTexture output);

    /// <summary>Loads anything size-dependent before the first frame, so loading does not stall the output.</summary>
    void Prepare(int width, int height) { }

    /// <summary>Called after a discontinuity (scene cut, source restart).</summary>
    void Reset();

    /// <summary>True when the last call fell back to a copy (shot change detected by the engine).</summary>
    bool LastWasCopy { get; }
}

/// <summary>Linear cross-fade. Not a real interpolator: used to exercise N > 1 pacing in tests.</summary>
public sealed class BlendGenerator : IFrameGenerator
{
    private readonly GraphicsDevice _g;
    private readonly ID3D11Buffer _cb;

    public string Id => "blend";
    public bool LastWasCopy => false;

    public BlendGenerator(GraphicsDevice g)
    {
        _g = g;
        _cb = g.CreateConstantBuffer(16);
    }

    public void Generate(RenderTexture previous, RenderTexture current, float t, RenderTexture output)
    {
        _g.Update(_cb, new Vector4(t, 0, 0, 0));
        _g.Draw(output.Rtv, _g.PixelShader("scale.hlsl", "PSBlend"), output.Viewport, [previous.Srv, current.Srv], null, _cb);
    }

    public void Reset() { }

    public void Dispose() => _cb.Dispose();
}

/// <summary>GPU time per stage from timestamp queries, read back a few frames later.</summary>
public sealed class GpuTimer : IDisposable
{
    private const int Depth = 5;
    private readonly ID3D11DeviceContext _ctx;
    private readonly Frame[] _frames;
    private int _write;
    private readonly string[] _stages;
    private int _openStage = -1;
    private readonly double[] _results;

    private sealed class Frame
    {
        public required ID3D11Query Disjoint;
        public required ID3D11Query[] Begin;
        public required ID3D11Query[] End;
        public required bool[] Used;
        public bool Pending;
    }

    /// <summary>Per-stage milliseconds; the array is reused, handlers must copy what they keep.</summary>
    public event Action<double[]>? Completed;

    public GpuTimer(ID3D11Device device, ID3D11DeviceContext ctx, params string[] stages)
    {
        _ctx = ctx;
        _stages = stages;
        _results = new double[stages.Length];
        _frames = new Frame[Depth];
        for (int i = 0; i < Depth; i++)
        {
            _frames[i] = new Frame
            {
                Disjoint = device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint)),
                Begin = stages.Select(_ => device.CreateQuery(new QueryDescription(QueryType.Timestamp))).ToArray(),
                End = stages.Select(_ => device.CreateQuery(new QueryDescription(QueryType.Timestamp))).ToArray(),
                Used = new bool[stages.Length],
            };
        }
    }

    private Frame Current => _frames[_write];

    public void BeginFrame()
    {
        Collect();
        var f = Current;
        if (f.Pending) return;
        Array.Clear(f.Used);
        _ctx.Begin(f.Disjoint);
    }

    public void BeginStage(int stage)
    {
        var f = Current;
        if (f.Pending) return;
        _openStage = stage;
        f.Used[stage] = true;
        _ctx.End(f.Begin[stage]);
    }

    public void EndStage()
    {
        var f = Current;
        if (f.Pending || _openStage < 0) return;
        _ctx.End(f.End[_openStage]);
        _openStage = -1;
    }

    public void EndFrame()
    {
        var f = Current;
        if (f.Pending) return;
        _ctx.End(f.Disjoint);
        // Without a flush the last timestamps wait in the command buffer until the next frame.
        _ctx.Flush();
        f.Pending = true;
        _write = (_write + 1) % Depth;
    }

    private void Collect()
    {
        for (int k = 0; k < Depth; k++)
        {
            var f = _frames[(_write + k) % Depth];
            if (!f.Pending) continue;
            if (!_ctx.IsDataAvailable(f.Disjoint, AsyncGetDataFlags.DoNotFlush)) continue;
            if (!_ctx.GetData(f.Disjoint, AsyncGetDataFlags.DoNotFlush, out QueryDataTimestampDisjoint dj)) continue;
            var ms = _results;
            bool ok = !dj.Disjoint;
            for (int s = 0; s < _stages.Length && ok; s++)
            {
                if (!f.Used[s]) { ms[s] = double.NaN; continue; }
                if (!_ctx.GetData(f.Begin[s], AsyncGetDataFlags.DoNotFlush, out ulong b) || !_ctx.GetData(f.End[s], AsyncGetDataFlags.DoNotFlush, out ulong e))
                {
                    ok = false;
                    break;
                }
                ms[s] = (e - b) * 1000.0 / dj.Frequency;
            }
            f.Pending = false;
            if (ok) Completed?.Invoke(ms);
        }
    }

    public void Dispose()
    {
        foreach (var f in _frames)
        {
            f.Disjoint.Dispose();
            foreach (var q in f.Begin) q.Dispose();
            foreach (var q in f.End) q.Dispose();
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct DiffConstants
{
    public Vector4 Size;
}

/// <summary>Block luma difference between two GPU frames (GPU sources), read back synchronously.</summary>
public sealed class GpuFrameDiff : IDisposable
{
    private readonly GraphicsDevice _g;
    private readonly RenderTexture _target;
    private readonly ID3D11Texture2D _staging;
    private readonly ID3D11Buffer _cb;

    public double LastMean { get; private set; }

    public GpuFrameDiff(GraphicsDevice g)
    {
        _g = g;
        _target = new RenderTexture(g.Device, 16, 9, Vortice.DXGI.Format.R32_Float);
        _staging = g.Device.CreateTexture2D(new Texture2DDescription(Vortice.DXGI.Format.R32_Float, 16, 9, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _cb = g.CreateConstantBuffer(16);
    }

    public unsafe double Compute(RenderTexture a, RenderTexture b)
    {
        _g.Update(_cb, new DiffConstants { Size = new Vector4(a.Width, a.Height, 0, 0) });
        _g.Draw(_target.Rtv, _g.PixelShader("util.hlsl", "PSDiff"), _target.Viewport, [a.Srv, b.Srv], null, _cb);
        _g.Context.CopyResource(_staging, _target.Texture);
        var map = _g.Context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        double max = 0, sum = 0;
        for (int y = 0; y < 9; y++)
        {
            float* row = (float*)((byte*)map.DataPointer + y * map.RowPitch);
            for (int x = 0; x < 16; x++)
            {
                max = Math.Max(max, row[x]);
                sum += row[x];
            }
        }
        _g.Context.Unmap(_staging, 0);
        LastMean = sum / 144;
        return max;
    }

    public void Dispose()
    {
        _target.Dispose();
        _staging.Dispose();
        _cb.Dispose();
    }
}
