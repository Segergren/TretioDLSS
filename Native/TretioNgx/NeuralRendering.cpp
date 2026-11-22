// DLSS 5 Neural Rendering on captured video: NGX feature 18 in nvngx_dlssnr.dll, driven directly on D3D12.
// Each layer is a feature of its own that reads the layer before it. Depth is constant and motion vectors are zero.
// The NGX core only hands out parameter blocks; every call into the runtime goes through TretioNr.nvngx.dll,
// because the runtime accepts only callers whose module path contains "nvngx.dll".
// The host shares an input texture, an output texture (both RGBA16F) and a fence with its D3D11 device.

#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <nvsdk_ngx.h>

#include "D3d12Util.h"

#include <algorithm>
#include <format>
#include <stdexcept>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

constexpr uint32_t kMaxLayers = 4;
constexpr unsigned long long kAppId = 0x54524554ull;

struct TnrDesc
{
    LUID adapter;
    uint32_t width;
    uint32_t height;
    uint32_t layers;
    uint32_t preset;
    const wchar_t* runtimePath;
    const wchar_t* callerPath;
    const wchar_t* dataDir;
    const char* projectId;
    TngxLog log;
    uint32_t verbose;
};

struct TnrShared
{
    HANDLE input;
    HANDLE output;
    HANDLE fence;
};

namespace {

TngxLog g_nrLog = nullptr;

void NrLog(int level, const std::string& message)
{
    if (g_nrLog) g_nrLog(level, message.c_str());
}

void NVSDK_CONV NgxCoreLog(const char* message, NVSDK_NGX_Logging_Level, NVSDK_NGX_Feature)
{
    if (message) NrLog(0, std::string("NGX: ") + message);
}

std::string Narrow(const wchar_t* w)
{
    char s[MAX_PATH * 2]{};
    WideCharToMultiByte(CP_UTF8, 0, w, -1, s, sizeof(s), nullptr, nullptr);
    return s;
}

typedef int(__cdecl* InitFn)(unsigned long long, const wchar_t*, ID3D12Device*, int, const void*);
typedef int(__cdecl* CreateFn)(ID3D12GraphicsCommandList*, int, void*, void**);
typedef int(__cdecl* EvaluateFn)(ID3D12GraphicsCommandList*, const void*, const void*, void*);
typedef int(__cdecl* ReleaseFn)(void*);
typedef int(__cdecl* ShutdownFn)(ID3D12Device*);
typedef int(__cdecl* CallInitFn)(InitFn, unsigned long long, const wchar_t*, ID3D12Device*, int, const void*, DWORD*);
typedef int(__cdecl* CallCreateFn)(CreateFn, ID3D12GraphicsCommandList*, void*, void**, DWORD*);
typedef int(__cdecl* CallEvaluateFn)(EvaluateFn, ID3D12GraphicsCommandList*, const void*, const void*, DWORD*);
typedef int(__cdecl* CallReleaseFn)(ReleaseFn, void*, DWORD*);
typedef int(__cdecl* CallShutdownFn)(ShutdownFn, ID3D12Device*, DWORD*);

// The runtime and the caller module stay loaded for the life of the process.
struct Runtime
{
    HMODULE snippet = nullptr, caller = nullptr;
    std::wstring path;
    InitFn init = nullptr;
    CreateFn create = nullptr;
    EvaluateFn evaluate = nullptr;
    ReleaseFn release = nullptr;
    ShutdownFn shutdown = nullptr;
    CallInitFn callInit = nullptr;
    CallCreateFn callCreate = nullptr;
    CallEvaluateFn callEvaluate = nullptr;
    CallReleaseFn callRelease = nullptr;
    CallShutdownFn callShutdown = nullptr;

    void Load(const wchar_t* runtimePath, const wchar_t* callerPath)
    {
        if (snippet)
        {
            if (_wcsicmp(path.c_str(), runtimePath) != 0)
                throw std::runtime_error(std::format("{} is already loaded; restart the app to use {}", Narrow(path.c_str()), Narrow(runtimePath)));
            return;
        }
        if (!caller)
        {
            caller = LoadLibraryExW(callerPath, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (!caller) throw std::runtime_error(std::format("{} did not load ({})", Narrow(callerPath), GetLastError()));
            callInit = (CallInitFn)GetProcAddress(caller, "TnrCallInit");
            callCreate = (CallCreateFn)GetProcAddress(caller, "TnrCallCreate");
            callEvaluate = (CallEvaluateFn)GetProcAddress(caller, "TnrCallEvaluate");
            callRelease = (CallReleaseFn)GetProcAddress(caller, "TnrCallRelease");
            callShutdown = (CallShutdownFn)GetProcAddress(caller, "TnrCallShutdown");
            if (!callInit || !callCreate || !callEvaluate || !callRelease || !callShutdown)
                throw std::runtime_error(std::format("{} lacks its entry points", Narrow(callerPath)));
        }
        HMODULE m = LoadLibraryExW(runtimePath, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!m) throw std::runtime_error(std::format("{} did not load ({})", Narrow(runtimePath), GetLastError()));
        init = (InitFn)GetProcAddress(m, "NVSDK_NGX_D3D12_Init_Ext");
        create = (CreateFn)GetProcAddress(m, "NVSDK_NGX_D3D12_CreateFeature");
        evaluate = (EvaluateFn)GetProcAddress(m, "NVSDK_NGX_D3D12_EvaluateFeature");
        release = (ReleaseFn)GetProcAddress(m, "NVSDK_NGX_D3D12_ReleaseFeature");
        shutdown = (ShutdownFn)GetProcAddress(m, "NVSDK_NGX_D3D12_Shutdown1");
        if (!init || !create || !evaluate || !release)
        {
            FreeLibrary(m);
            throw std::runtime_error(std::format("{} is not a DLSS 5 runtime (D3D12 entry points missing)", Narrow(runtimePath)));
        }
        snippet = m;
        path = runtimePath;
    }
};

Runtime g_rt;

std::string FileVersion(const wchar_t* path)
{
    DWORD handle = 0, size = GetFileVersionInfoSizeW(path, &handle);
    std::vector<uint8_t> info(size);
    VS_FIXEDFILEINFO* v = nullptr;
    UINT len = 0;
    if (size && GetFileVersionInfoW(path, 0, size, info.data()) && VerQueryValueW(info.data(), L"\\", (void**)&v, &len) && v)
        return std::format("{}.{}.{}", HIWORD(v->dwFileVersionMS), LOWORD(v->dwFileVersionMS), HIWORD(v->dwFileVersionLS));
    return "?";
}

__declspec(noinline) NVSDK_NGX_Result SafeCoreInit(const char* projectId, const wchar_t* dataDir, ID3D12Device* device, const NVSDK_NGX_FeatureCommonInfo* info, DWORD& seh)
{
    seh = 0;
    __try { return NVSDK_NGX_D3D12_Init_with_ProjectID(projectId, NVSDK_NGX_ENGINE_TYPE_CUSTOM, "1.0", dataDir, device, info, NVSDK_NGX_Version_API); }
    __except (EXCEPTION_EXECUTE_HANDLER) { seh = GetExceptionCode(); return NVSDK_NGX_Result_FAIL_PlatformError; }
}

} // namespace

struct TnrSession
{
    uint32_t w = 0, h = 0, layers = 1, preset = 0;
    ComPtr<ID3D12Device> dev;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> alloc[2];
    ComPtr<ID3D12GraphicsCommandList> list;
    ComPtr<ID3D12Fence> shared, done;
    uint64_t doneValue = 0, allocDone[2] = {};
    int slot = 0;
    HANDLE event = nullptr;

    ComPtr<ID3D12Resource> input, output;
    Tracked color, depth, mvec, out[kMaxLayers];
    NVSDK_NGX_Parameter* params[kMaxLayers] = {};
    void* feature[kMaxLayers] = {};
    bool ngxInit = false, runtimeInit = false, failed = false;
    uint64_t frames = 0;

    ComPtr<ID3D12QueryHeap> timestamps;
    ComPtr<ID3D12Resource> timestampReadback[2];
    bool timestampPending[2] = {};
    uint64_t timestampFrequency = 0;
    std::vector<double> totalMs;
    double layerMs[kMaxLayers] = {};
    uint64_t layerSamples = 0;

    void Check(HRESULT hr, const char* what)
    {
        if (FAILED(hr)) throw std::runtime_error(std::format("{} failed {}", what, Hex((uint32_t)hr)));
    }

    void Idle()
    {
        if (!queue || !done) return;
        queue->Signal(done.Get(), ++doneValue);
        if (done->GetCompletedValue() >= doneValue) return;
        done->SetEventOnCompletion(doneValue, event);
        WaitForSingleObject(event, 5000);
    }

    ID3D12GraphicsCommandList* Begin()
    {
        if (done->GetCompletedValue() < allocDone[slot])
        {
            done->SetEventOnCompletion(allocDone[slot], event);
            WaitForSingleObject(event, 5000);
        }
        alloc[slot]->Reset();
        list->Reset(alloc[slot].Get(), nullptr);
        return list.Get();
    }

    void Submit()
    {
        list->Close();
        ID3D12CommandList* lists[] = { list.Get() };
        queue->ExecuteCommandLists(1, lists);
        queue->Signal(done.Get(), ++doneValue);
        allocDone[slot] = doneValue;
        slot ^= 1;
    }

    ComPtr<ID3D12Resource> Texture(DXGI_FORMAT format, D3D12_RESOURCE_FLAGS flags, bool sharedHeap = false)
    {
        D3D12_HEAP_PROPERTIES hp{ D3D12_HEAP_TYPE_DEFAULT };
        D3D12_RESOURCE_DESC d{};
        d.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        d.Width = w;
        d.Height = h;
        d.DepthOrArraySize = 1;
        d.MipLevels = 1;
        d.Format = format;
        d.SampleDesc.Count = 1;
        d.Flags = flags;
        ComPtr<ID3D12Resource> r;
        // Committed resources start zeroed, which is the constant depth and the zero motion the model gets.
        Check(dev->CreateCommittedResource(&hp, sharedHeap ? D3D12_HEAP_FLAG_SHARED : D3D12_HEAP_FLAG_NONE, &d, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&r)), "CreateCommittedResource");
        return r;
    }

    void SetCreateParams(NVSDK_NGX_Parameter* p)
    {
        p->Set(NVSDK_NGX_Parameter_CreationNodeMask, 1u);
        p->Set(NVSDK_NGX_Parameter_VisibilityNodeMask, 1u);
        p->Set(NVSDK_NGX_Parameter_Width, w);
        p->Set(NVSDK_NGX_Parameter_Height, h);
        p->Set(NVSDK_NGX_Parameter_OutWidth, w);
        p->Set(NVSDK_NGX_Parameter_OutHeight, h);
        p->Set(NVSDK_NGX_Parameter_PerfQualityValue, 2u);
        p->Set("DLSSNR.Enabled", 1u);
        p->Set("DLSSNR.Width", w);
        p->Set("DLSSNR.Height", h);
        p->Set("DLSSNR.Hint.Render.Preset", preset);
        p->Set("DLSSNR.Intensity", 1.0f);
        p->Set("DLSSNR.Style", 0u);
        p->Set("DLSSNR.LocalStructureStrength", 1.0f);
        p->Set("DLSSNR.LocalToneStrength", 1.0f);
        p->Set("DLSSNR.UseAutoMask", 0u);
        p->Set("DLSSNR.UICorrection", 0u);
    }

    void SetFixedEvaluateParams(NVSDK_NGX_Parameter* p)
    {
        p->Set("DLSSNR.Depth", depth.res.Get());
        p->Set("DLSSNR.MVec", mvec.res.Get());
        p->Set("DLSSNR.DepthInverted", 0u);
        p->Set("DLSSNR.ColorSubrectBaseX", 0u);
        p->Set("DLSSNR.ColorSubrectBaseY", 0u);
        p->Set("DLSSNR.ColorSubrectWidth", w);
        p->Set("DLSSNR.ColorSubrectHeight", h);
        p->Set("DLSSNR.OutputSubrectBaseX", 0u);
        p->Set("DLSSNR.OutputSubrectBaseY", 0u);
        p->Set("DLSSNR.OutputSubrectWidth", w);
        p->Set("DLSSNR.OutputSubrectHeight", h);
        p->Set("DLSSNR.DepthSubrectBaseX", 0u);
        p->Set("DLSSNR.DepthSubrectBaseY", 0u);
        p->Set("DLSSNR.DepthSubrectWidth", w);
        p->Set("DLSSNR.DepthSubrectHeight", h);
        p->Set("DLSSNR.MVecSubrectBaseX", 0u);
        p->Set("DLSSNR.MVecSubrectBaseY", 0u);
        p->Set("DLSSNR.MVecSubrectWidth", w);
        p->Set("DLSSNR.MVecSubrectHeight", h);
        p->Set("DLSSNR.MVecScaleX", 1.0f);
        p->Set("DLSSNR.MVecScaleY", 1.0f);
    }

    void Create(const TnrDesc& d, TnrShared& out);
    bool Evaluate(uint64_t inputReady, bool reset, uint32_t style, float intensity, uint64_t& outputReady);
    void ReadTimestamps(int s);

    bool Fail(uint64_t inputReady, uint64_t& outputReady)
    {
        failed = true;
        queue->Signal(shared.Get(), inputReady + 1);
        outputReady = inputReady + 1;
        return false;
    }

    ~TnrSession();
};

void TnrSession::Create(const TnrDesc& d, TnrShared& sharedOut)
{
    w = d.width;
    h = d.height;
    layers = std::clamp(d.layers, 1u, kMaxLayers);
    preset = d.preset;
    event = CreateEventW(nullptr, FALSE, FALSE, nullptr);

    ComPtr<IDXGIFactory4> factory;
    Check(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)), "CreateDXGIFactory2");
    ComPtr<IDXGIAdapter1> adapter;
    Check(factory->EnumAdapterByLuid(d.adapter, IID_PPV_ARGS(&adapter)), "EnumAdapterByLuid");
    DXGI_ADAPTER_DESC1 ad{};
    adapter->GetDesc1(&ad);
    Check(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&dev)), "D3D12CreateDevice");
    char name[128]{};
    WideCharToMultiByte(CP_UTF8, 0, ad.Description, -1, name, sizeof(name), nullptr, nullptr);
    NrLog(1, std::format("D3D12 device on {} (device 0x{:04X}) for {}x{}, {} layer(s), preset {}", name, ad.DeviceId, w, h, layers, preset));

    D3D12_COMMAND_QUEUE_DESC qd{ D3D12_COMMAND_LIST_TYPE_DIRECT };
    Check(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue)), "CreateCommandQueue");
    for (auto& a : alloc) Check(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&a)), "CreateCommandAllocator");
    Check(dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc[0].Get(), nullptr, IID_PPV_ARGS(&list)), "CreateCommandList");
    list->Close();
    Check(dev->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&shared)), "CreateFence shared");
    Check(dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&done)), "CreateFence");
    queue->GetTimestampFrequency(&timestampFrequency);
    D3D12_QUERY_HEAP_DESC qh{ D3D12_QUERY_HEAP_TYPE_TIMESTAMP, kMaxLayers + 2 };
    Check(dev->CreateQueryHeap(&qh, IID_PPV_ARGS(&timestamps)), "CreateQueryHeap");
    for (auto& rb : timestampReadback)
    {
        D3D12_HEAP_PROPERTIES hp{ D3D12_HEAP_TYPE_READBACK };
        D3D12_RESOURCE_DESC bd{};
        bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        bd.Width = (kMaxLayers + 2) * sizeof(uint64_t);
        bd.Height = 1;
        bd.DepthOrArraySize = 1;
        bd.MipLevels = 1;
        bd.SampleDesc.Count = 1;
        bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        Check(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&rb)), "CreateCommittedResource timestamps");
    }

    auto half = DXGI_FORMAT_R16G16B16A16_FLOAT;
    auto sharedFlags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS;
    auto uav = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    input = Texture(half, sharedFlags, true);
    output = Texture(half, sharedFlags, true);
    color.res = Texture(half, uav);
    depth.res = Texture(DXGI_FORMAT_R32_FLOAT, uav);
    mvec.res = Texture(DXGI_FORMAT_R16G16_FLOAT, uav);
    for (uint32_t i = 0; i < layers; i++) out[i].res = Texture(half, uav);

    // The core's path list holds only the data folder, so it never loads a runtime of its own beside ours.
    const wchar_t* paths[] = { d.dataDir };
    NVSDK_NGX_FeatureCommonInfo info{};
    info.PathListInfo.Path = paths;
    info.PathListInfo.Length = 1;
    if (d.verbose)
    {
        info.LoggingInfo.LoggingCallback = NgxCoreLog;
        info.LoggingInfo.MinimumLoggingLevel = NVSDK_NGX_LOGGING_LEVEL_ON;
        info.LoggingInfo.DisableOtherLoggingSinks = true;
    }
    DWORD seh = 0;
    NVSDK_NGX_Result r = SafeCoreInit(d.projectId, d.dataDir, dev.Get(), &info, seh);
    if (r != NVSDK_NGX_Result_Success) throw std::runtime_error(std::format("NGX init failed {} (exception {})", Hex(r), Hex(seh)));
    ngxInit = true;
    for (uint32_t i = 0; i < layers; i++)
    {
        r = NVSDK_NGX_D3D12_AllocateParameters(&params[i]);
        if (r != NVSDK_NGX_Result_Success || !params[i]) throw std::runtime_error(std::format("NGX AllocateParameters failed {}", Hex(r)));
    }

    if (d.verbose) SetEnvironmentVariableW(L"__NGX_LOG_LEVEL", L"1");
    g_rt.Load(d.runtimePath, d.callerPath);
    NrLog(1, std::format("runtime {} from {}", FileVersion(d.runtimePath), Narrow(d.runtimePath)));
    int ir = g_rt.callInit(g_rt.init, kAppId, d.dataDir, dev.Get(), NVSDK_NGX_Version_API, params[0], &seh);
    if (ir != NVSDK_NGX_Result_Success) throw std::runtime_error(std::format("runtime init failed {} (exception {})", Hex((uint32_t)ir), Hex(seh)));
    runtimeInit = true;

    LARGE_INTEGER qpf, t0, t1;
    QueryPerformanceFrequency(&qpf);
    QueryPerformanceCounter(&t0);
    auto* l = Begin();
    for (uint32_t i = 0; i < layers; i++)
    {
        SetCreateParams(params[i]);
        int cr = g_rt.callCreate(g_rt.create, l, params[i], &feature[i], &seh);
        if (cr != NVSDK_NGX_Result_Success || !feature[i])
        {
            feature[i] = nullptr;
            Submit();
            Idle();
            throw std::runtime_error(std::format("create of layer {} failed {} (exception {})", i + 1, Hex((uint32_t)cr), Hex(seh)));
        }
        SetFixedEvaluateParams(params[i]);
    }
    Submit();
    Idle();
    QueryPerformanceCounter(&t1);
    NrLog(1, std::format("{} layer(s) created in {:.0f} ms", layers, (t1.QuadPart - t0.QuadPart) * 1000.0 / qpf.QuadPart));

    Check(dev->CreateSharedHandle(input.Get(), nullptr, GENERIC_ALL, nullptr, &sharedOut.input), "CreateSharedHandle input");
    Check(dev->CreateSharedHandle(output.Get(), nullptr, GENERIC_ALL, nullptr, &sharedOut.output), "CreateSharedHandle output");
    Check(dev->CreateSharedHandle(shared.Get(), nullptr, GENERIC_ALL, nullptr, &sharedOut.fence), "CreateSharedHandle fence");
}

void TnrSession::ReadTimestamps(int s)
{
    if (!timestampPending[s]) return;
    timestampPending[s] = false;
    uint64_t* t = nullptr;
    D3D12_RANGE range{ 0, (layers + 2) * sizeof(uint64_t) };
    if (FAILED(timestampReadback[s]->Map(0, &range, (void**)&t))) return;
    if (t[layers + 1] > t[0] && totalMs.size() < 100000)
    {
        totalMs.push_back((t[layers + 1] - t[0]) * 1000.0 / timestampFrequency);
        for (uint32_t i = 0; i < layers; i++) layerMs[i] += (t[i + 1] - t[i]) * 1000.0 / timestampFrequency;
        layerSamples++;
    }
    D3D12_RANGE none{ 0, 0 };
    timestampReadback[s]->Unmap(0, &none);
}

bool TnrSession::Evaluate(uint64_t inputReady, bool reset, uint32_t style, float intensity, uint64_t& outputReady)
{
    queue->Wait(shared.Get(), inputReady);
    if (failed) return Fail(inputReady, outputReady);

    auto* l = Begin();
    // Begin waited for this slot's last submission, so its timestamps are complete.
    ReadTimestamps(slot);
    l->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0);
    color.To(l, D3D12_RESOURCE_STATE_COPY_DEST);
    l->CopyResource(color.res.Get(), input.Get());
    depth.To(l, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    mvec.To(l, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    for (uint32_t i = 0; i < layers; i++)
    {
        Tracked& in = i == 0 ? color : out[i - 1];
        in.To(l, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        out[i].To(l, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        auto* p = params[i];
        p->Set("DLSSNR.Color", in.res.Get());
        p->Set("DLSSNR.Output", out[i].res.Get());
        p->Set("DLSSNR.Enabled", 1u);
        p->Set("DLSSNR.Reset", reset ? 1u : 0u);
        p->Set("DLSSNR.Style", style);
        p->Set("DLSSNR.Intensity", intensity);
        DWORD seh = 0;
        int r = g_rt.callEvaluate(g_rt.evaluate, l, feature[i], p, &seh);
        if (r != NVSDK_NGX_Result_Success)
        {
            NrLog(2, std::format("evaluate of layer {} failed {} (exception {}) at frame {}", i + 1, Hex((uint32_t)r), Hex(seh), frames));
            l->Close();
            return Fail(inputReady, outputReady);
        }
        l->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, i + 1);
    }
    out[layers - 1].To(l, D3D12_RESOURCE_STATE_COPY_SOURCE);
    l->CopyResource(output.Get(), out[layers - 1].res.Get());
    l->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, layers + 1);
    l->ResolveQueryData(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0, layers + 2, timestampReadback[slot].Get(), 0);
    timestampPending[slot] = true;
    Submit();
    queue->Signal(shared.Get(), inputReady + 1);
    outputReady = inputReady + 1;
    frames++;
    return true;
}

TnrSession::~TnrSession()
{
    Idle();
    if (totalMs.size() > 10)
    {
        std::sort(totalMs.begin(), totalMs.end());
        std::string perLayer;
        for (uint32_t i = 0; i < layers; i++) perLayer += std::format("{}{:.2f}", i ? ", " : "", layerMs[i] / std::max<uint64_t>(1, layerSamples));
        NrLog(1, std::format("GPU time per frame for {} layer(s) at {}x{}: p50 {:.2f} ms, p95 {:.2f} ms over {} frames; per layer {} ms",
            layers, w, h, totalMs[totalMs.size() / 2], totalMs[totalMs.size() * 95 / 100], totalMs.size(), perLayer));
    }
    DWORD seh = 0;
    for (uint32_t i = 0; i < kMaxLayers; i++)
        if (feature[i]) g_rt.callRelease(g_rt.release, feature[i], &seh);
    if (runtimeInit && g_rt.shutdown) g_rt.callShutdown(g_rt.shutdown, dev.Get(), &seh);
    for (auto* p : params)
        if (p) NVSDK_NGX_D3D12_DestroyParameters(p);
    if (ngxInit) NVSDK_NGX_D3D12_Shutdown1(dev.Get());
    if (event) CloseHandle(event);
}

extern "C" __declspec(dllexport) int __cdecl TnrCreate(const TnrDesc* desc, TnrSession** session, TnrShared* shared)
{
    g_nrLog = desc->log;
    *session = nullptr;
    *shared = {};
    auto* s = new TnrSession();
    try
    {
        s->Create(*desc, *shared);
        *session = s;
        return 0;
    }
    catch (const std::exception& e)
    {
        NrLog(2, e.what());
        for (HANDLE hnd : { shared->input, shared->output, shared->fence })
            if (hnd) CloseHandle(hnd);
        *shared = {};
        delete s;
        return 1;
    }
}

extern "C" __declspec(dllexport) int __cdecl TnrEvaluate(TnrSession* session, uint64_t inputReady, int reset, uint32_t style, float intensity, uint64_t* outputReady)
{
    return session->Evaluate(inputReady, reset != 0, style, intensity, *outputReady) ? 0 : 1;
}

extern "C" __declspec(dllexport) void __cdecl TnrDestroy(TnrSession* session)
{
    delete session;
}
