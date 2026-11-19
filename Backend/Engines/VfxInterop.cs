using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace TretioDLSS.Engines;

/// <summary>NvCVImage from the Video Effects SDK (nvCVImage.h), 64 bytes on x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NvCVImage
{
    public uint Width, Height;
    public int Pitch;
    public int PixelFormat, ComponentType;
    public byte PixelBytes, ComponentBytes, NumComponents, Planar, GpuMem, Colorspace, Reserved0, Reserved1;
    public IntPtr Pixels, DeletePtr, DeleteProc;
    public ulong BufferBytes;
}

/// <summary>
/// One Video Effects SDK effect with its CUDA stream. D3D11 textures are mapped into
/// CUDA with NvCVImage; the map and unmap order the CUDA work against the D3D11 context.
/// </summary>
internal sealed unsafe class VfxEffect : IDisposable
{
    private const int NVCV_RGBA = 6, NVCV_U8 = 1, NVCV_INTERLEAVED = 0, NVCV_GPU = 1;

    private readonly Api _api;
    private readonly string _name;
    public string Dir { get; }
    public IntPtr Stream { get; private set; }
    private IntPtr _effect;

    public VfxEffect(string dir, string selector, string name)
    {
        Dir = dir;
        _name = name;
        _api = Api.Open(dir);
        WithDllDir(() =>
        {
            IntPtr s, e;
            Check(_api.CudaStreamCreate(&s), "NvVFX_CudaStreamCreate");
            Stream = s;
            Check(_api.CreateEffect(Native(selector), &e), "NvVFX_CreateEffect");
            _effect = e;
        });
    }

    public void SetU32(string param, uint value) => Check(_api.SetU32(_effect, Native(param), value), param);
    public void SetF32(string param, float value) => Check(_api.SetF32(_effect, Native(param), value), param);
    public void SetImage(string param, NvCVImage* image) => Check(_api.SetImage(_effect, Native(param), image), param);
    public void SetStream() => Check(_api.SetCudaStream(_effect, Native("CudaStream"), Stream), "CudaStream");
    public void Run() => Check(_api.Run(_effect, 0), "NvVFX_Run");

    /// <summary>Loads the model; the feature DLL is found next to the runtime.</summary>
    public void Load() => WithDllDir(() => Check(_api.Load(_effect), "NvVFX_Load"));

    public void Map(NvCVImage* image) => Check(_api.MapResource(image, Stream), "NvCVImage_MapResource");
    public void Unmap(NvCVImage* image) => _api.UnmapResource(image, Stream);
    public void Transfer(NvCVImage* src, NvCVImage* dst, NvCVImage* tmp) => Check(_api.Transfer(src, dst, 1f, Stream, tmp), "NvCVImage_Transfer");

    public static NvCVImage* NewImage() => (NvCVImage*)NativeMemory.AllocZeroed((nuint)sizeof(NvCVImage));

    public NvCVImage* FromTexture(ID3D11Texture2D tex)
    {
        var p = NewImage();
        Check(_api.InitFromD3D11Texture(p, tex.NativePointer), "NvCVImage_InitFromD3D11Texture");
        return p;
    }

    /// <summary>An RGBA 8-bit buffer in GPU memory, the layout both effects take.</summary>
    public NvCVImage* GpuImage(int w, int h)
    {
        var p = NewImage();
        Check(_api.Alloc(p, (uint)w, (uint)h, NVCV_RGBA, NVCV_U8, NVCV_INTERLEAVED, NVCV_GPU, 32), "NvCVImage_Alloc");
        return p;
    }

    public void Free(ref NvCVImage* p)
    {
        if (p == null) return;
        _api.Dealloc(p);
        NativeMemory.Free(p);
        p = null;
    }

    private void Check(int status, string what)
    {
        if (status == 0) return;
        string text = "";
        try { text = Marshal.PtrToStringAnsi(_api.ErrorString(status)) ?? ""; } catch { }
        throw new InvalidOperationException($"{_name}: {what} failed with {status} {text}".Trim());
    }

    public void Dispose()
    {
        if (_effect != IntPtr.Zero) _api.DestroyEffect(_effect);
        if (Stream != IntPtr.Zero) _api.CudaStreamDestroy(Stream);
        _effect = IntPtr.Zero;
        Stream = IntPtr.Zero;
    }

    private void WithDllDir(Action action) => WithDllDir(Dir, action);

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectoryW(string? path);

    private static void WithDllDir(string dir, Action action)
    {
        SetDllDirectoryW(dir);
        try { action(); }
        finally { SetDllDirectoryW(null); }
    }

    /// <summary>Selector and parameter names as native strings, kept for the life of the process.</summary>
    private static readonly ConcurrentDictionary<string, IntPtr> Names = new();

    private static IntPtr Native(string s) => Names.GetOrAdd(s, Marshal.StringToHGlobalAnsi);

    private sealed class Api
    {
        public required delegate* unmanaged<IntPtr, IntPtr*, int> CreateEffect;
        public required delegate* unmanaged<IntPtr, void> DestroyEffect;
        public required delegate* unmanaged<IntPtr, IntPtr, uint, int> SetU32;
        public required delegate* unmanaged<IntPtr, IntPtr, float, int> SetF32;
        public required delegate* unmanaged<IntPtr, IntPtr, NvCVImage*, int> SetImage;
        public required delegate* unmanaged<IntPtr, IntPtr, IntPtr, int> SetCudaStream;
        public required delegate* unmanaged<IntPtr, int> Load;
        public required delegate* unmanaged<IntPtr, int, int> Run;
        public required delegate* unmanaged<IntPtr*, int> CudaStreamCreate;
        public required delegate* unmanaged<IntPtr, int> CudaStreamDestroy;
        public required delegate* unmanaged<NvCVImage*, uint, uint, int, int, uint, uint, uint, int> Alloc;
        public required delegate* unmanaged<NvCVImage*, void> Dealloc;
        public required delegate* unmanaged<NvCVImage*, IntPtr, int> InitFromD3D11Texture;
        public required delegate* unmanaged<NvCVImage*, IntPtr, int> MapResource;
        public required delegate* unmanaged<NvCVImage*, IntPtr, int> UnmapResource;
        public required delegate* unmanaged<NvCVImage*, NvCVImage*, float, IntPtr, NvCVImage*, int> Transfer;
        public required delegate* unmanaged<int, IntPtr> ErrorString;

        private static Api? _loaded;
        private static readonly object Gate = new();

        public static Api Open(string dir)
        {
            lock (Gate)
            {
                if (_loaded != null) return _loaded;
                IntPtr cv = 0, fx = 0;
                WithDllDir(dir, () =>
                {
                    cv = NativeLibrary.Load(Path.Combine(dir, "NVCVImage.dll"));
                    fx = NativeLibrary.Load(Path.Combine(dir, "NVVideoEffects.dll"));
                });
                IntPtr F(IntPtr lib, string name) => NativeLibrary.GetExport(lib, name);
                return _loaded = new Api
                {
                    CreateEffect = (delegate* unmanaged<IntPtr, IntPtr*, int>)F(fx, "NvVFX_CreateEffect"),
                    DestroyEffect = (delegate* unmanaged<IntPtr, void>)F(fx, "NvVFX_DestroyEffect"),
                    SetU32 = (delegate* unmanaged<IntPtr, IntPtr, uint, int>)F(fx, "NvVFX_SetU32"),
                    SetF32 = (delegate* unmanaged<IntPtr, IntPtr, float, int>)F(fx, "NvVFX_SetF32"),
                    SetImage = (delegate* unmanaged<IntPtr, IntPtr, NvCVImage*, int>)F(fx, "NvVFX_SetImage"),
                    SetCudaStream = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)F(fx, "NvVFX_SetCudaStream"),
                    Load = (delegate* unmanaged<IntPtr, int>)F(fx, "NvVFX_Load"),
                    Run = (delegate* unmanaged<IntPtr, int, int>)F(fx, "NvVFX_Run"),
                    CudaStreamCreate = (delegate* unmanaged<IntPtr*, int>)F(fx, "NvVFX_CudaStreamCreate"),
                    CudaStreamDestroy = (delegate* unmanaged<IntPtr, int>)F(fx, "NvVFX_CudaStreamDestroy"),
                    Alloc = (delegate* unmanaged<NvCVImage*, uint, uint, int, int, uint, uint, uint, int>)F(cv, "NvCVImage_Alloc"),
                    Dealloc = (delegate* unmanaged<NvCVImage*, void>)F(cv, "NvCVImage_Dealloc"),
                    InitFromD3D11Texture = (delegate* unmanaged<NvCVImage*, IntPtr, int>)F(cv, "NvCVImage_InitFromD3D11Texture"),
                    MapResource = (delegate* unmanaged<NvCVImage*, IntPtr, int>)F(cv, "NvCVImage_MapResource"),
                    UnmapResource = (delegate* unmanaged<NvCVImage*, IntPtr, int>)F(cv, "NvCVImage_UnmapResource"),
                    Transfer = (delegate* unmanaged<NvCVImage*, NvCVImage*, float, IntPtr, NvCVImage*, int>)F(cv, "NvCVImage_Transfer"),
                    ErrorString = (delegate* unmanaged<int, IntPtr>)F(cv, "NvCV_GetErrorStringFromCode"),
                };
            }
        }
    }
}
