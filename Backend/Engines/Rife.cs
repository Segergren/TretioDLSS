using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Serilog;
using TretioDLSS.App;
using TretioDLSS.Graphics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TretioDLSS.Engines;

[StructLayout(LayoutKind.Sequential)]
internal struct RifeConstants
{
    public Vector2 ModelSize;
    public Vector2 FlowSize;
    public float T;
    public Vector3 Pad;
}

/// <summary>
/// RIFE frame generation on any GPU: ONNX Runtime with DirectML
/// on a D3D12 device of its own, fed through shared textures and a shared fence, no CPU copies.
/// The model returns motion and a blend mask; the warp and blend run here at full resolution.
/// </summary>
public sealed class RifeFrameGenerator : IFrameGenerator
{
    private static readonly (int W, int H)[] Sizes = [(2560, 1440), (1920, 1080), (1280, 720)];

    private readonly GraphicsDevice _g;
    private readonly string _stem;
    private readonly BlendGenerator _fallback;
    private volatile RifeIsland? _island;
    private Task? _building;
    private int _w, _h;
    private readonly List<double> _callMs = new();

    public string Id => "rife";
    public bool LastWasCopy => false;

    public static IEnumerable<string> ModelFiles()
    {
        var dirs = new List<string> { AppInfo.ModelsFolder };
        if (AppInfo.RepoRoot is string root) dirs.Add(Path.Combine(root, "ThirdParty", "models"));
        foreach (var d in dirs.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(d, "rife*_*x*.onnx", SearchOption.AllDirectories))
                yield return f;
    }

    /// <summary>Model families with at least one size on disk: rife46 (fast), rife426 (quality).</summary>
    public static List<string> Models() =>
        ModelFiles().Select(f => Path.GetFileNameWithoutExtension(f).Split('_')[0]).Distinct().OrderBy(s => s).ToList();

    public static string ModelLabel(string stem) => stem switch
    {
        "rife46" => "Fast (RIFE 4.6)",
        "rife426" => "Quality (RIFE 4.26)",
        _ => stem,
    };

    private static bool? _runtimeOk;

    /// <summary>ONNX Runtime with DirectML loads on this machine.</summary>
    public static bool RuntimeAvailable()
    {
        if (_runtimeOk is bool known) return known;
        try
        {
            Log.Information("ONNX Runtime {Version} with DirectML for RIFE", OrtDml.Version);
            _runtimeOk = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ONNX Runtime with DirectML is not available");
            _runtimeOk = false;
        }
        return _runtimeOk.Value;
    }

    public static IFrameGenerator? TryCreate(GraphicsDevice g, string model)
    {
        var models = Models();
        if (models.Count == 0 || !RuntimeAvailable())
        {
            Log.Warning("RIFE has no model or no runtime; frame generation stays off");
            return null;
        }
        string stem = models.Contains(model) ? model : models.Contains("rife426") ? "rife426" : models[0];
        return new RifeFrameGenerator(g, stem);
    }

    private RifeFrameGenerator(GraphicsDevice g, string stem)
    {
        _g = g;
        _stem = stem;
        _fallback = new BlendGenerator(g);
        Log.Information("RIFE frame generation, model {Model}", stem);
    }

    /// <summary>The model size for a frame: the largest exported size that fits, else the smallest.</summary>
    private string ModelPath(int w, int h)
    {
        var files = ModelFiles().Where(f => Path.GetFileName(f).StartsWith(_stem + "_")).ToList();
        foreach (var (mw, mh) in Sizes)
        {
            if (mw > w || mh > h) continue;
            var f = files.FirstOrDefault(x => x.EndsWith($"_{mw}x{mh}.onnx"));
            if (f != null) return f;
        }
        return files.OrderBy(f => new FileInfo(f).Length).First();
    }

    public void Prepare(int width, int height)
    {
        if (width == _w && height == _h && (_island != null || _building != null)) return;
        _w = width;
        _h = height;
        var old = _island;
        _island = null;
        string path = ModelPath(width, height);
        _building = Task.Run(() =>
        {
            try
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                var island = new RifeIsland(_g, path);
                if (_w == width && _h == height) _island = island;
                else island.Dispose();
                Log.Information("RIFE ready: {Model} for {W} x {H} frames in {Ms} ms", Path.GetFileName(path), width, height, started.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "RIFE failed to start; blending instead");
            }
        });
        old?.Dispose();
    }

    public void Generate(RenderTexture previous, RenderTexture current, float t, RenderTexture output)
    {
        if (_w != current.Width || _h != current.Height) Prepare(current.Width, current.Height);
        var island = _island;
        if (island == null)
        {
            _fallback.Generate(previous, current, t, output);
            return;
        }
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        island.Generate(previous, current, t, output);
        if (_callMs.Count < 100_000) _callMs.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    public void Reset() => _island?.ForgetPair();

    public void Dispose()
    {
        if (_callMs.Count > 0)
        {
            var sorted = _callMs.Order().ToList();
            Log.Information("RIFE: {Count} frames, CPU time per call p50 {P50:F2} ms, p99 {P99:F2} ms, max {Max:F2} ms",
                sorted.Count, sorted[sorted.Count / 2], sorted[(int)(sorted.Count * 0.99)], sorted[^1]);
        }
        try { _building?.Wait(10000); } catch { }
        _island?.Dispose();
        _island = null;
        _fallback.Dispose();
    }
}

/// <summary>The D3D12 side for one model size: device, queue, shared textures and fence, tensors, session.</summary>
internal sealed class RifeIsland : IDisposable
{

    private readonly GraphicsDevice _g;
    private readonly int _mw, _mh;
    private readonly ID3D12Device _dev;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12Fence _fence12;
    private readonly ID3D11Fence _fence11;
    private readonly ID3D11DeviceContext4 _ctx4;
    private readonly ID3D12CommandAllocator _allocPre, _allocPost;
    private readonly ID3D12GraphicsCommandList _pre, _post;
    private readonly ID3D12Resource _in0Tex, _in1Tex, _motionTex, _in0Buf, _in1Buf, _outBuf;
    private readonly ID3D11Texture2D _in0, _in1, _motion;
    private readonly ID3D11RenderTargetView _in0Rtv, _in1Rtv;
    private readonly ID3D11ShaderResourceView _motionSrv;
    private readonly ID3D11Buffer _scaleCb, _rifeCb;
    private readonly IntPtr _dmlDevice, _alloc0, _alloc1, _allocOut;
    private readonly InferenceSession _session;
    private readonly OrtValue _v0, _v1, _vOut;
    private readonly RunOptions _run = new();
    private readonly string[] _inNames = ["img0", "img1"], _outNames = ["out"];
    private readonly IntPtr _event = CreateEventW(IntPtr.Zero, false, false, null);
    private ulong _value, _lastDone;
    private IntPtr _pairA, _pairB;
    private int _pairs;
    private long _pairAt;

    public RifeIsland(GraphicsDevice g, string modelPath)
    {
        _g = g;
        var name = Path.GetFileNameWithoutExtension(modelPath).Split('_')[1].Split('x');
        _mw = int.Parse(name[0]);
        _mh = int.Parse(name[1]);

        D3D12.D3D12CreateDevice(g.Adapter, FeatureLevel.Level_11_0, out ID3D12Device? dev).CheckError();
        _dev = dev!;
        _queue = _dev.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        _fence12 = _dev.CreateFence(0, Vortice.Direct3D12.FenceFlags.Shared);
        _fence11 = OpenFence(g, _fence12);
        _ctx4 = g.Context.QueryInterface<ID3D11DeviceContext4>();
        _allocPre = _dev.CreateCommandAllocator(CommandListType.Direct);
        _allocPost = _dev.CreateCommandAllocator(CommandListType.Direct);
        _pre = _dev.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _allocPre, null);
        _post = _dev.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _allocPost, null);

        var shared = new HeapProperties(HeapType.Default);
        var texFlags = ResourceFlags.AllowRenderTarget | ResourceFlags.AllowSimultaneousAccess;
        _in0Tex = _dev.CreateCommittedResource(shared, HeapFlags.Shared, ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, (uint)_mw, (uint)_mh, 1, 1, 1, 0, texFlags), ResourceStates.Common);
        _in1Tex = _dev.CreateCommittedResource(shared, HeapFlags.Shared, ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, (uint)_mw, (uint)_mh, 1, 1, 1, 0, texFlags), ResourceStates.Common);
        // Motion: [H/2, W/2, 8] halves, read as RGBA16F texels, two per motion sample.
        _motionTex = _dev.CreateCommittedResource(shared, HeapFlags.Shared, ResourceDescription.Texture2D(Format.R16G16B16A16_Float, (uint)_mw, (uint)(_mh / 2), 1, 1, 1, 0, texFlags), ResourceStates.Common);
        ulong bytes = (ulong)_mw * (ulong)_mh * 4;
        _in0Buf = _dev.CreateCommittedResource(shared, HeapFlags.None, ResourceDescription.Buffer(bytes, ResourceFlags.AllowUnorderedAccess), ResourceStates.Common);
        _in1Buf = _dev.CreateCommittedResource(shared, HeapFlags.None, ResourceDescription.Buffer(bytes, ResourceFlags.AllowUnorderedAccess), ResourceStates.Common);
        _outBuf = _dev.CreateCommittedResource(shared, HeapFlags.None, ResourceDescription.Buffer(bytes, ResourceFlags.AllowUnorderedAccess), ResourceStates.Common);

        _in0 = OpenTexture(g, _in0Tex);
        _in1 = OpenTexture(g, _in1Tex);
        _motion = OpenTexture(g, _motionTex);
        _in0Rtv = g.Device.CreateRenderTargetView(_in0);
        _in1Rtv = g.Device.CreateRenderTargetView(_in1);
        _motionSrv = g.Device.CreateShaderResourceView(_motion);
        _scaleCb = g.CreateConstantBuffer(Marshal.SizeOf<ScaleConstants>());
        _rifeCb = g.CreateConstantBuffer(Marshal.SizeOf<RifeConstants>());

        // DirectML wants its buffers in the unordered access state.
        _pre.ResourceBarrierTransition(_in0Buf, ResourceStates.Common, ResourceStates.UnorderedAccess);
        _pre.ResourceBarrierTransition(_in1Buf, ResourceStates.Common, ResourceStates.UnorderedAccess);
        _pre.ResourceBarrierTransition(_outBuf, ResourceStates.Common, ResourceStates.UnorderedAccess);
        _pre.Close();
        _post.Close();
        _queue.ExecuteCommandList(_pre);
        WaitIdle();

        _dmlDevice = OrtDml.CreateDmlDevice(_dev.NativePointer);
        using (var options = new SessionOptions())
        {
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            OrtDml.AppendProvider(options.DangerousGetHandle(), _dmlDevice, _queue.NativePointer);
            _session = new InferenceSession(modelPath, options);
        }
        _alloc0 = OrtDml.Wrap(_in0Buf.NativePointer);
        _alloc1 = OrtDml.Wrap(_in1Buf.NativePointer);
        _allocOut = OrtDml.Wrap(_outBuf.NativePointer);
        using var dml = new OrtMemoryInfo("DML", OrtAllocatorType.DeviceAllocator, 0, OrtMemType.Default);
        long[] inShape = [1, _mh, _mw, 4], outShape = [1, _mh / 2, _mw / 2, 8];
        _v0 = OrtValue.CreateTensorValueWithData(dml, Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.UInt8, inShape, _alloc0, (long)bytes);
        _v1 = OrtValue.CreateTensorValueWithData(dml, Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.UInt8, inShape, _alloc1, (long)bytes);
        _vOut = OrtValue.CreateTensorValueWithData(dml, Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Float16, outShape, _allocOut, (long)bytes);

        // The first run compiles DirectML's operators; do it here, not on the first real frame.
        RunModel();
        WaitIdle();
    }

    public void ForgetPair() => _pairA = _pairB = IntPtr.Zero;

    public void Generate(RenderTexture previous, RenderTexture current, float t, RenderTexture output)
    {
        // Frames of one pair arrive back to back; the motion is computed once per pair.
        long now = Environment.TickCount64;
        bool samePair = previous.Texture.NativePointer == _pairA && current.Texture.NativePointer == _pairB && now - _pairAt < 200;
        if (!samePair)
        {
            ScaleInto(previous, _in0Rtv);
            ScaleInto(current, _in1Rtv);
            ulong ready = ++_value;
            _ctx4.Signal(_fence11, ready);
            _g.Context.Flush();
            _queue.Wait(_fence12, ready);
            RunModel();
            _ctx4.Wait(_fence11, _value);
            if (++_pairs == 30 && Environment.GetEnvironmentVariable("TRETIO_RIFE_DUMP") is string dumpDir) DumpTensors(dumpDir);
            _pairA = previous.Texture.NativePointer;
            _pairB = current.Texture.NativePointer;
        }
        _pairAt = now;

        _g.Update(_rifeCb, new RifeConstants
        {
            ModelSize = new Vector2(_mw, _mh),
            FlowSize = new Vector2(_mw / 2, _mh / 2),
            T = Math.Clamp(t, 0f, 1f),
        });
        _g.Draw(output.Rtv, _g.PixelShader("rife.hlsl", "PSRifeWarp"), output.Viewport, [previous.Srv, current.Srv, _motionSrv], _rifeCb);
    }

    /// <summary>Copies the inputs into the tensors, runs the model and copies the motion out, all on the D3D12 queue.</summary>
    private void RunModel()
    {
        WaitFor(_lastDone);
        _allocPre.Reset();
        _pre.Reset(_allocPre, null);
        CopyIn(_in0Tex, _in0Buf);
        CopyIn(_in1Tex, _in1Buf);
        _pre.Close();
        _queue.ExecuteCommandList(_pre);

        _session.Run(_run, _inNames, [_v0, _v1], _outNames, [_vOut]);

        _allocPost.Reset();
        _post.Reset(_allocPost, null);
        _post.ResourceBarrierTransition(_outBuf, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        var footprint = new PlacedSubresourceFootPrint { Offset = 0, Footprint = new SubresourceFootPrint(Format.R16G16B16A16_Float, (uint)_mw, (uint)(_mh / 2), 1, (uint)_mw * 8) };
        _post.CopyTextureRegion(new TextureCopyLocation(_motionTex, 0), 0, 0, 0, new TextureCopyLocation(_outBuf, footprint), null);
        _post.ResourceBarrierTransition(_outBuf, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
        _post.Close();
        _queue.ExecuteCommandList(_post);
        _lastDone = ++_value;
        _queue.Signal(_fence12, _lastDone);
    }

    private void CopyIn(ID3D12Resource tex, ID3D12Resource buf)
    {
        _pre.ResourceBarrierTransition(buf, ResourceStates.UnorderedAccess, ResourceStates.CopyDest);
        var footprint = new PlacedSubresourceFootPrint { Offset = 0, Footprint = new SubresourceFootPrint(Format.R8G8B8A8_UNorm, (uint)_mw, (uint)_mh, 1, (uint)_mw * 4) };
        _pre.CopyTextureRegion(new TextureCopyLocation(buf, footprint), 0, 0, 0, new TextureCopyLocation(tex, 0), null);
        _pre.ResourceBarrierTransition(buf, ResourceStates.CopyDest, ResourceStates.UnorderedAccess);
    }

    private void ScaleInto(RenderTexture src, ID3D11RenderTargetView target)
    {
        _g.Update(_scaleCb, new ScaleConstants
        {
            SrcSize = new Vector4(src.Width, src.Height, 1f / src.Width, 1f / src.Height),
            SrcRect = new Vector4(0, 0, 1, 1),
        });
        _g.Draw(target, _g.PixelShader("scale.hlsl", "PSBilinear"), new Viewport(0, 0, _mw, _mh), [src.Srv], _scaleCb);
    }

    /// <summary>Diagnostics: writes the model's inputs and output as raw files (TRETIO_RIFE_DUMP).</summary>
    private unsafe void DumpTensors(string dir)
    {
        WaitIdle();
        Directory.CreateDirectory(dir);
        ulong bytes = (ulong)_mw * (ulong)_mh * 4;
        using var readback = _dev.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None, ResourceDescription.Buffer(bytes), ResourceStates.CopyDest);
        foreach (var (buf, name) in new[] { (_in0Buf, "in0"), (_in1Buf, "in1"), (_outBuf, "out") })
        {
            _allocPost.Reset();
            _post.Reset(_allocPost, null);
            _post.ResourceBarrierTransition(buf, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            _post.CopyBufferRegion(readback, 0, buf, 0, bytes);
            _post.ResourceBarrierTransition(buf, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
            _post.Close();
            _queue.ExecuteCommandList(_post);
            WaitIdle();
            void* data;
            readback.Map(0, null, &data).CheckError();
            File.WriteAllBytes(Path.Combine(dir, $"{name}_{_mw}x{_mh}.bin"), new ReadOnlySpan<byte>(data, (int)bytes).ToArray());
            readback.Unmap(0);
        }
        Log.Information("RIFE tensors written to {Dir}", dir);
    }

    private void WaitFor(ulong value)
    {
        if (value == 0 || _fence12.CompletedValue >= value) return;
        _fence12.SetEventOnCompletion(value, _event);
        WaitForSingleObject(_event, 2000);
    }

    private void WaitIdle()
    {
        _queue.Signal(_fence12, ++_value);
        WaitFor(_value);
    }

    private ID3D11Texture2D OpenTexture(GraphicsDevice g, ID3D12Resource resource)
    {
        IntPtr handle = _dev.CreateSharedHandle(resource, null, null!);
        try { return g.Device1.OpenSharedResource1<ID3D11Texture2D>(handle); }
        finally { CloseHandle(handle); }
    }

    private ID3D11Fence OpenFence(GraphicsDevice g, ID3D12Fence fence)
    {
        IntPtr handle = _dev.CreateSharedHandle(fence, null, null!);
        try
        {
            using var dev5 = g.Device.QueryInterface<ID3D11Device5>();
            return dev5.OpenSharedFence<ID3D11Fence>(handle);
        }
        finally { CloseHandle(handle); }
    }

    public void Dispose()
    {
        try { WaitIdle(); } catch { }
        _v0.Dispose();
        _v1.Dispose();
        _vOut.Dispose();
        _session.Dispose();
        _run.Dispose();
        OrtDml.Free(_alloc0);
        OrtDml.Free(_alloc1);
        OrtDml.Free(_allocOut);
        if (_dmlDevice != IntPtr.Zero) Marshal.Release(_dmlDevice);
        _in0Rtv.Dispose();
        _in1Rtv.Dispose();
        _motionSrv.Dispose();
        _in0.Dispose();
        _in1.Dispose();
        _motion.Dispose();
        _scaleCb.Dispose();
        _rifeCb.Dispose();
        _in0Buf.Dispose();
        _in1Buf.Dispose();
        _outBuf.Dispose();
        _in0Tex.Dispose();
        _in1Tex.Dispose();
        _motionTex.Dispose();
        _pre.Dispose();
        _post.Dispose();
        _allocPre.Dispose();
        _allocPost.Dispose();
        _fence11.Dispose();
        _ctx4.Dispose();
        _fence12.Dispose();
        _queue.Dispose();
        _dev.Dispose();
        CloseHandle(_event);
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32")]
    private static extern bool CloseHandle(IntPtr handle);
}
