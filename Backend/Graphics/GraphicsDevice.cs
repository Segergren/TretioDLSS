using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using TretioDLSS.App;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TretioDLSS.Graphics;

/// <summary>
/// The pipeline's D3D11 device. One immediate context, used only by the pipeline thread.
/// </summary>
public sealed class GraphicsDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11Device1 Device1 { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIFactory2 Factory { get; }
    public IDXGIAdapter1 Adapter { get; }
    public string AdapterName { get; }
    public bool IsNvidia { get; }
    public bool IsAmd { get; }
    public ID3D11SamplerState LinearClamp { get; }
    public ID3D11SamplerState PointClamp { get; }
    public ID3D11VertexShader FullscreenVS { get; }

    private readonly Dictionary<(string File, string Entry), ID3D11PixelShader> _pixelShaders = new();

    private GraphicsDevice(IDXGIFactory2 factory, IDXGIAdapter1 adapter, ID3D11Device device, ID3D11DeviceContext context)
    {
        Factory = factory;
        Adapter = adapter;
        Device = device;
        Device1 = device.QueryInterface<ID3D11Device1>();
        Context = context;
        var desc = adapter.Description1;
        AdapterName = desc.Description;
        IsNvidia = desc.VendorId == 0x10DE;
        IsAmd = desc.VendorId == 0x1002;

        using (var mt = device.QueryInterface<ID3D11Multithread>())
            mt.SetMultithreadProtected(true);

        LinearClamp = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        PointClamp = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
        FullscreenVS = device.CreateVertexShader(Compile("common.hlsli", "VSFullscreen", "vs_5_0").Span);
    }

    /// <summary>NVIDIA adapter if present, else the high-performance one; an explicit name overrides.</summary>
    public static GraphicsDevice Create(string adapterPreference = "auto", bool debug = false)
    {
        var factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
        IDXGIAdapter1? chosen = null;
        var all = new List<IDXGIAdapter1>();
        for (uint i = 0; factory.EnumAdapterByGpuPreference(i, GpuPreference.HighPerformance, out IDXGIAdapter1? a).Success; i++)
        {
            if (a == null) break;
            if ((a.Description1.Flags & AdapterFlags.Software) != 0) { a.Dispose(); continue; }
            all.Add(a);
        }
        if (all.Count == 0) throw new InvalidOperationException("No graphics adapter found");
        if (adapterPreference != "auto")
            chosen = all.FirstOrDefault(a => a.Description1.Description.Contains(adapterPreference, StringComparison.OrdinalIgnoreCase));
        chosen ??= all.FirstOrDefault(a => a.Description1.VendorId == 0x10DE) ?? all[0];
        foreach (var a in all.Where(a => a != chosen)) a.Dispose();

        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        if (debug && D3D11.SdkLayersAvailable()) flags |= DeviceCreationFlags.Debug;
        D3D11.D3D11CreateDevice(chosen, DriverType.Unknown, flags, [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        Log.Information("Graphics device on {Adapter}", chosen.Description1.Description);
        return new GraphicsDevice(factory, chosen, device!, context!);
    }

    public void SetGpuThreadPriority(int priority)
    {
        try
        {
            using var dxgi = Device.QueryInterface<IDXGIDevice>();
            dxgi.SetGPUThreadPriority(priority);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SetGPUThreadPriority failed");
        }
    }

    public ID3D11PixelShader PixelShader(string file, string entry)
    {
        var key = (file, entry);
        if (_pixelShaders.TryGetValue(key, out var ps)) return ps;
        ps = Device.CreatePixelShader(Compile(file, entry, "ps_5_0").Span);
        _pixelShaders[key] = ps;
        return ps;
    }

    private readonly Dictionary<(string File, string Entry, string Defines), ID3D11ComputeShader> _computeShaders = new();

    public ID3D11ComputeShader ComputeShader(string file, string entry, string defines = "")
    {
        var key = (file, entry, defines);
        if (_computeShaders.TryGetValue(key, out var cs)) return cs;
        cs = Device.CreateComputeShader(Compile(file, entry, "cs_5_0", defines).Span);
        _computeShaders[key] = cs;
        return cs;
    }

    // ---- shader compilation with an on-disk cache ----

    private static readonly Assembly Asm = typeof(GraphicsDevice).Assembly;

    // Resource names keep the directory separator MSBuild used; look them up with forward slashes.
    private static readonly Dictionary<string, string> ResourceNames =
        Asm.GetManifestResourceNames().ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

    public static string LoadShaderSource(string name) => Expand(name, new HashSet<string>());

    private static string Expand(string name, HashSet<string> seen)
    {
        string resource = "Shaders/" + name.Replace('\\', '/');
        using var stream = (ResourceNames.TryGetValue(resource, out var real) ? Asm.GetManifestResourceStream(real) : null)
            ?? throw new FileNotFoundException("Shader not embedded: " + resource);
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();
        string dir = name.Contains('/') ? name[..(name.LastIndexOf('/') + 1)] : "";
        return Regex.Replace(text, "^\\s*#include\\s+\"([^\"]+)\"", m =>
        {
            string inc = m.Groups[1].Value;
            string path = inc.Contains('/') ? inc : dir + inc;
            if (!ResourceNames.ContainsKey("Shaders/" + path)) path = inc;
            return seen.Add(path) ? Expand(path, seen) : "";
        }, RegexOptions.Multiline);
    }

    public static ReadOnlyMemory<byte> Compile(string file, string entry, string profile, string defines = "")
    {
        string source = defines + "\n" + LoadShaderSource(file);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + entry + profile)))[..24];
        string cachePath = Path.Combine(AppInfo.ShaderCache, $"{Path.GetFileNameWithoutExtension(file)}_{entry}_{hash}.cso");
        try
        {
            if (File.Exists(cachePath)) return File.ReadAllBytes(cachePath);
        }
        catch { }

        var result = Compiler.Compile(source, entry, file, profile, out Blob? blob, out Blob? error);
        if (result.Failure || blob == null)
        {
            string message = error?.AsString() ?? result.ToString();
            error?.Dispose();
            throw new InvalidOperationException($"Shader {file}:{entry} failed: {message}");
        }
        error?.Dispose();
        var bytes = blob.AsBytes();
        blob.Dispose();
        try
        {
            Directory.CreateDirectory(AppInfo.ShaderCache);
            File.WriteAllBytes(cachePath, bytes);
        }
        catch { }
        return bytes;
    }

    // ---- small helpers used by all stages ----

    public ID3D11Buffer CreateConstantBuffer(int size) =>
        Device.CreateBuffer(new BufferDescription((uint)((size + 15) & ~15), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

    public unsafe void Update<T>(ID3D11Buffer buffer, in T data) where T : unmanaged
    {
        var mapped = Context.Map(buffer, MapMode.WriteDiscard);
        *(T*)mapped.DataPointer = data;
        Context.Unmap(buffer, 0);
    }

    /// <summary>Draws a fullscreen triangle with the given pixel shader into a render target viewport.</summary>
    public void Draw(ID3D11RenderTargetView target, ID3D11PixelShader ps, Viewport viewport, ReadOnlySpan<ID3D11ShaderResourceView?> srvs, params ReadOnlySpan<ID3D11Buffer?> cbs)
    {
        var ctx = Context;
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetInputLayout(null);
        ctx.VSSetShader(FullscreenVS);
        ctx.PSSetShader(ps);
        ctx.PSSetSampler(0, LinearClamp);
        ctx.PSSetSampler(1, PointClamp);
        for (int i = 0; i < srvs.Length; i++) ctx.PSSetShaderResource((uint)i, srvs[i]!);
        for (int i = 0; i < cbs.Length; i++) if (cbs[i] != null) ctx.PSSetConstantBuffer((uint)i, cbs[i]!);
        ctx.RSSetViewport(viewport);
        ctx.OMSetRenderTargets(target);
        ctx.Draw(3, 0);
        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        for (int i = 0; i < srvs.Length; i++) ctx.PSUnsetShaderResource((uint)i);
    }

    public void Dispose()
    {
        foreach (var ps in _pixelShaders.Values) ps.Dispose();
        FullscreenVS.Dispose();
        LinearClamp.Dispose();
        PointClamp.Dispose();
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device1.Dispose();
        Device.Dispose();
        Adapter.Dispose();
        Factory.Dispose();
    }
}

/// <summary>A texture with the views the stages need.</summary>
public sealed class RenderTexture : IDisposable
{
    public ID3D11Texture2D Texture { get; }
    public ID3D11ShaderResourceView Srv { get; }
    public ID3D11RenderTargetView Rtv { get; }
    public int Width { get; }
    public int Height { get; }
    public Format Format { get; }

    /// <summary>Set by the pipeline to the real frame index the texture holds; 0 when unknown.</summary>
    public long Stamp { get; set; }

    public RenderTexture(ID3D11Device device, int width, int height, Format format = Format.R8G8B8A8_UNorm, bool shared = false)
    {
        Width = width;
        Height = height;
        Format = format;
        var desc = new Texture2DDescription(format, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget);
        if (shared) desc.MiscFlags = ResourceOptionFlags.Shared;
        Texture = device.CreateTexture2D(desc);
        Srv = device.CreateShaderResourceView(Texture);
        Rtv = device.CreateRenderTargetView(Texture);
    }

    public Viewport Viewport => new(0, 0, Width, Height);

    public void Dispose()
    {
        Rtv.Dispose();
        Srv.Dispose();
        Texture.Dispose();
    }
}
