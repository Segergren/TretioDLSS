using System.Runtime.InteropServices;

namespace TretioDLSS.Engines;

/// <summary>
/// The parts of ONNX Runtime's DirectML API the C# package does not expose:
/// our own D3D12 device and queue for the session, and D3D12 buffers as tensors. Indexes are
/// from onnxruntime_c_api.h and dml_provider_factory.h of 1.24.4 (ORT_API_VERSION 24).
/// </summary>
internal static unsafe class OrtDml
{
    private const uint ApiVersion = 24;
    private const int GetErrorMessageIndex = 2, ReleaseStatusIndex = 93, GetExecutionProviderApiIndex = 195;

    [StructLayout(LayoutKind.Sequential)]
    private struct OrtApiBase
    {
        public delegate* unmanaged<uint, IntPtr> GetApi;
        public delegate* unmanaged<IntPtr> GetVersionString;
    }

    [DllImport("onnxruntime", CallingConvention = CallingConvention.Cdecl)]
    private static extern OrtApiBase* OrtGetApiBase();

    [DllImport("DirectML", CallingConvention = CallingConvention.StdCall)]
    private static extern int DMLCreateDevice(IntPtr d3d12Device, uint flags, in Guid riid, out IntPtr device);

    private static readonly Guid IID_IDMLDevice = new("6dbd6437-96fd-423f-a98c-ae5e7c2a573f");

    private static IntPtr* _api;
    private static IntPtr* _dml;

    private static void Ensure()
    {
        if (_dml != null) return;
        var baseApi = OrtGetApiBase();
        _api = (IntPtr*)baseApi->GetApi(ApiVersion);
        if (_api == null) throw new InvalidOperationException("This ONNX Runtime does not offer API version 24");
        var getProviderApi = (delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr>)_api[GetExecutionProviderApiIndex];
        IntPtr name = Marshal.StringToHGlobalAnsi("DML");
        try
        {
            IntPtr dml;
            Check(getProviderApi(name, ApiVersion, &dml));
            _dml = (IntPtr*)dml;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    private static void Check(IntPtr status)
    {
        if (status == IntPtr.Zero) return;
        var message = ((delegate* unmanaged<IntPtr, IntPtr>)_api[GetErrorMessageIndex])(status);
        string text = Marshal.PtrToStringAnsi(message) ?? "unknown error";
        ((delegate* unmanaged<IntPtr, void>)_api[ReleaseStatusIndex])(status);
        throw new InvalidOperationException("ONNX Runtime DirectML: " + text);
    }

    public static string Version
    {
        get
        {
            var baseApi = OrtGetApiBase();
            return Marshal.PtrToStringAnsi(baseApi->GetVersionString()) ?? "";
        }
    }

    /// <summary>A DirectML device on our D3D12 device; the caller releases it.</summary>
    public static IntPtr CreateDmlDevice(IntPtr d3d12Device)
    {
        int hr = DMLCreateDevice(d3d12Device, 0, IID_IDMLDevice, out IntPtr device);
        if (hr < 0) throw new InvalidOperationException($"DMLCreateDevice failed: 0x{hr:X8}");
        return device;
    }

    /// <summary>SessionOptionsAppendExecutionProvider_DML1: the session runs on our device and queue.</summary>
    public static void AppendProvider(IntPtr sessionOptions, IntPtr dmlDevice, IntPtr queue)
    {
        Ensure();
        Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)_dml[1])(sessionOptions, dmlDevice, queue));
    }

    /// <summary>CreateGPUAllocationFromD3DResource: a D3D12 buffer as tensor memory.</summary>
    public static IntPtr Wrap(IntPtr d3d12Resource)
    {
        Ensure();
        IntPtr allocation;
        Check(((delegate* unmanaged<IntPtr, IntPtr*, IntPtr>)_dml[2])(d3d12Resource, &allocation));
        return allocation;
    }

    /// <summary>FreeGPUAllocation.</summary>
    public static void Free(IntPtr allocation)
    {
        if (allocation == IntPtr.Zero || _dml == null) return;
        Check(((delegate* unmanaged<IntPtr, IntPtr>)_dml[3])(allocation));
    }
}
