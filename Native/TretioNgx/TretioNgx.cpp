// TretioNgx: DLSS Frame Generation on captured video, through NGX on D3D12.
// Motion vectors are zero and depth is constant: the runtime estimates motion from the frames itself.
// x3 and x4 on RTX 40 come from MfgUnlock, patched into the runtime before NGX starts.
// The host shares an input texture, an output texture and a fence with its D3D11 device.

#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <nvsdk_ngx.h>
#include <nvsdk_ngx_defs_dlssg.h>
#include <nvsdk_ngx_helpers_dlssg.h>

#include "MfgUnlock.h"

#include <cstdint>
#include <cstring>
#include <algorithm>
#include <format>
#include <stdexcept>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

typedef void(__cdecl* TngxLog)(int level, const char* message);

struct TngxDesc
{
    LUID adapter;
    uint32_t width;
    uint32_t height;
    const wchar_t* runtimeDir;
    const wchar_t* dataDir;
    const char* projectId;
    TngxLog log;
    uint32_t verbose;
    uint32_t frames;
};

constexpr int kMaxFrames = 3;

struct TngxShared
{
    HANDLE input;
    HANDLE output[kMaxFrames];
    HANDLE fence;
    uint32_t frames;
};

TngxLog g_log = nullptr;

void Log(int level, const std::string& message)
{
    if (g_log) g_log(level, message.c_str());
}

namespace {

void NVSDK_CONV NgxLog(const char* message, NVSDK_NGX_Logging_Level, NVSDK_NGX_Feature)
{
    if (message) Log(0, std::string("NGX: ") + message);
}

std::string Hex(uint64_t v) { return std::format("0x{:X}", v); }

struct Tracked
{
    ComPtr<ID3D12Resource> res;
    D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_COMMON;

    void To(ID3D12GraphicsCommandList* list, D3D12_RESOURCE_STATES next)
    {
        if (state == next) return;
        D3D12_RESOURCE_BARRIER b{};
        b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource = res.Get();
        b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        b.Transition.StateBefore = state;
        b.Transition.StateAfter = next;
        list->ResourceBarrier(1, &b);
        state = next;
    }
};

__declspec(noinline) NVSDK_NGX_Result SafeInit(const char* projectId, const wchar_t* dataDir, ID3D12Device* device, const NVSDK_NGX_FeatureCommonInfo* info, DWORD& seh)
{
    seh = 0;
    __try { return NVSDK_NGX_D3D12_Init_with_ProjectID(projectId, NVSDK_NGX_ENGINE_TYPE_CUSTOM, "1.0", dataDir, device, info, NVSDK_NGX_Version_API); }
    __except (EXCEPTION_EXECUTE_HANDLER) { seh = GetExceptionCode(); return NVSDK_NGX_Result_FAIL_PlatformError; }
}

__declspec(noinline) NVSDK_NGX_Result SafeCreate(ID3D12GraphicsCommandList* list, NVSDK_NGX_Handle** handle, NVSDK_NGX_Parameter* params, NVSDK_NGX_DLSSG_Create_Params* create, DWORD& seh)
{
    seh = 0;
    __try { return NGX_D3D12_CREATE_DLSSG(list, 1, 1, handle, params, create); }
    __except (EXCEPTION_EXECUTE_HANDLER) { seh = GetExceptionCode(); return NVSDK_NGX_Result_FAIL_PlatformError; }
}

__declspec(noinline) NVSDK_NGX_Result SafeEvaluate(ID3D12GraphicsCommandList* list, NVSDK_NGX_Handle* handle, NVSDK_NGX_Parameter* params, NVSDK_NGX_D3D12_DLSSG_Eval_Params* eval, NVSDK_NGX_DLSSG_Opt_Eval_Params* opt, DWORD& seh)
{
    seh = 0;
    __try { return NGX_D3D12_EVALUATE_DLSSG(list, handle, params, eval, opt); }
    __except (EXCEPTION_EXECUTE_HANDLER) { seh = GetExceptionCode(); return NVSDK_NGX_Result_FAIL_PlatformError; }
}

} // namespace

struct TngxSession
{
    uint32_t w = 0, h = 0;
    ComPtr<ID3D12Device> dev;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> alloc[2];
    ComPtr<ID3D12GraphicsCommandList> list;
    ComPtr<ID3D12Fence> shared, done;
    uint64_t doneValue = 0, allocDone[2] = {};
    int slot = 0;
    HANDLE event = nullptr;

    ComPtr<ID3D12Resource> input, output[kMaxFrames];
    Tracked backbuffer, gen[kMaxFrames], mv, depth;
    uint32_t frames = 1;
    HMODULE runtime = nullptr;
    bool unlocked = false;
    ComPtr<ID3D12QueryHeap> timestamps;
    ComPtr<ID3D12Resource> timestampReadback;
    uint64_t timestampFrequency = 0;
    std::vector<double> gpuMs;

    bool ngxInit = false;
    NVSDK_NGX_Parameter* params = nullptr;
    NVSDK_NGX_Handle* fg = nullptr;
    uint64_t frameId = 0;
    bool failed = false;

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
        Check(dev->CreateCommittedResource(&hp, sharedHeap ? D3D12_HEAP_FLAG_SHARED : D3D12_HEAP_FLAG_NONE, &d, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&r)), "CreateCommittedResource");
        return r;
    }

    void Fill(ID3D12GraphicsCommandList* l, Tracked& t, ComPtr<ID3D12Resource>& upload, uint32_t value)
    {
        D3D12_RESOURCE_DESC d = t.res->GetDesc();
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp{};
        UINT64 bytes = 0;
        dev->GetCopyableFootprints(&d, 0, 1, 0, &fp, nullptr, nullptr, &bytes);
        D3D12_HEAP_PROPERTIES hp{ D3D12_HEAP_TYPE_UPLOAD };
        D3D12_RESOURCE_DESC bd{};
        bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        bd.Width = bytes;
        bd.Height = 1;
        bd.DepthOrArraySize = 1;
        bd.MipLevels = 1;
        bd.SampleDesc.Count = 1;
        bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        Check(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&upload)), "CreateCommittedResource upload");
        uint8_t* data = nullptr;
        Check(upload->Map(0, nullptr, (void**)&data), "Map");
        for (uint32_t y = 0; y < h; y++)
        {
            auto* row = (uint32_t*)(data + fp.Offset + (size_t)y * fp.Footprint.RowPitch);
            for (uint32_t x = 0; x < w; x++) row[x] = value;
        }
        upload->Unmap(0, nullptr);
        t.To(l, D3D12_RESOURCE_STATE_COPY_DEST);
        D3D12_TEXTURE_COPY_LOCATION dst{ t.res.Get(), D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX }, src{ upload.Get(), D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT };
        src.PlacedFootprint = fp;
        l->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        t.To(l, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    }

    void Unlock(const wchar_t* runtimeDir)
    {
        std::wstring path = std::wstring(runtimeDir) + L"\\nvngx_dlssg.dll";
        runtime = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!runtime)
        {
            Log(2, std::format("x3/x4 unlock: nvngx_dlssg.dll did not load ({}); x2 only", GetLastError()));
            frames = 1;
            return;
        }
        std::string detail;
        unlocked = MfgUnlock::Apply(runtime, detail);
        Log(unlocked ? 1 : 2, std::format("x3/x4 unlock for RTX 40 {}: {}", unlocked ? "applied" : "refused, x2 only", detail));
        if (!unlocked) frames = 1;
    }

    void Create(const TngxDesc& d, TngxShared& out);
    void CreateFrameGeneration(const TngxDesc& d);
    void LogRuntime();
    bool Evaluate(uint64_t inputReady, bool reset, uint64_t& outputReady);

    bool Fail(uint64_t inputReady, uint64_t& outputReady)
    {
        failed = true;
        queue->Signal(shared.Get(), inputReady + 1);
        outputReady = inputReady + 1;
        return false;
    }

    ~TngxSession();
};

void TngxSession::Create(const TngxDesc& d, TngxShared& out)
{
    w = d.width;
    h = d.height;
    frames = d.frames < 1 ? 1 : d.frames > kMaxFrames ? kMaxFrames : d.frames;
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
    Log(1, std::format("D3D12 device on {} (device 0x{:04X}) for {}x{}, {} generated per real frame requested", name, ad.DeviceId, w, h, frames));
    if (frames > 1 && MfgUnlock::IsAda(ad.VendorId, ad.DeviceId)) Unlock(d.runtimeDir);

    D3D12_COMMAND_QUEUE_DESC qd{ D3D12_COMMAND_LIST_TYPE_DIRECT };
    Check(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue)), "CreateCommandQueue");
    for (auto& a : alloc) Check(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&a)), "CreateCommandAllocator");
    Check(dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc[0].Get(), nullptr, IID_PPV_ARGS(&list)), "CreateCommandList");
    list->Close();
    Check(dev->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&shared)), "CreateFence shared");
    Check(dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&done)), "CreateFence");
    queue->GetTimestampFrequency(&timestampFrequency);
    D3D12_QUERY_HEAP_DESC qh{ D3D12_QUERY_HEAP_TYPE_TIMESTAMP, 4 };
    Check(dev->CreateQueryHeap(&qh, IID_PPV_ARGS(&timestamps)), "CreateQueryHeap");
    {
        D3D12_HEAP_PROPERTIES hp{ D3D12_HEAP_TYPE_READBACK };
        D3D12_RESOURCE_DESC bd{};
        bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        bd.Width = 4 * sizeof(uint64_t);
        bd.Height = 1;
        bd.DepthOrArraySize = 1;
        bd.MipLevels = 1;
        bd.SampleDesc.Count = 1;
        bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        Check(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&timestampReadback)), "CreateCommittedResource timestamps");
    }

    auto rgba = DXGI_FORMAT_R8G8B8A8_UNORM;
    auto sharedFlags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS;
    CreateFrameGeneration(d);

    input = Texture(rgba, sharedFlags, true);
    backbuffer.res = Texture(rgba, D3D12_RESOURCE_FLAG_NONE);
    for (uint32_t i = 0; i < frames; i++)
    {
        output[i] = Texture(rgba, sharedFlags, true);
        gen[i].res = Texture(rgba, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
    }
    mv.res = Texture(DXGI_FORMAT_R16G16_FLOAT, D3D12_RESOURCE_FLAG_NONE);
    depth.res = Texture(DXGI_FORMAT_R32_FLOAT, D3D12_RESOURCE_FLAG_NONE);

    ComPtr<ID3D12Resource> uploadMv, uploadDepth;
    float farDepth = 0.9f;
    uint32_t farBits;
    memcpy(&farBits, &farDepth, 4);
    auto* l = Begin();
    Fill(l, mv, uploadMv, 0);
    Fill(l, depth, uploadDepth, farBits);
    Submit();
    Idle();

    Check(dev->CreateSharedHandle(input.Get(), nullptr, GENERIC_ALL, nullptr, &out.input), "CreateSharedHandle input");
    for (uint32_t i = 0; i < frames; i++)
        Check(dev->CreateSharedHandle(output[i].Get(), nullptr, GENERIC_ALL, nullptr, &out.output[i]), "CreateSharedHandle output");
    out.frames = frames;
    Check(dev->CreateSharedHandle(shared.Get(), nullptr, GENERIC_ALL, nullptr, &out.fence), "CreateSharedHandle fence");
}

void TngxSession::CreateFrameGeneration(const TngxDesc& d)
{
    const wchar_t* paths[] = { d.runtimeDir };
    NVSDK_NGX_FeatureCommonInfo info{};
    info.PathListInfo.Path = paths;
    info.PathListInfo.Length = 1;
    if (d.verbose)
    {
        info.LoggingInfo.LoggingCallback = NgxLog;
        info.LoggingInfo.MinimumLoggingLevel = NVSDK_NGX_LOGGING_LEVEL_ON;
        info.LoggingInfo.DisableOtherLoggingSinks = true;
    }
    DWORD seh = 0;
    NVSDK_NGX_Result r = SafeInit(d.projectId, d.dataDir, dev.Get(), &info, seh);
    if (r != NVSDK_NGX_Result_Success) throw std::runtime_error(std::format("NGX init failed {} (exception {})", Hex(r), Hex(seh)));
    ngxInit = true;

    NVSDK_NGX_Parameter* caps = nullptr;
    r = NVSDK_NGX_D3D12_GetCapabilityParameters(&caps);
    if (r != NVSDK_NGX_Result_Success || !caps) throw std::runtime_error(std::format("NGX capability query failed {}", Hex(r)));
    unsigned int available = 0, needsDriver = 0, major = 0, minor = 0, multiMax = 0;
    int initResult = 0;
    caps->Get(NVSDK_NGX_Parameter_FrameGeneration_Available, &available);
    caps->Get(NVSDK_NGX_Parameter_FrameGeneration_NeedsUpdatedDriver, &needsDriver);
    caps->Get(NVSDK_NGX_Parameter_FrameGeneration_MinDriverVersionMajor, &major);
    caps->Get(NVSDK_NGX_Parameter_FrameGeneration_MinDriverVersionMinor, &minor);
    caps->Get(NVSDK_NGX_Parameter_FrameGeneration_FeatureInitResult, &initResult);
    caps->Get(NVSDK_NGX_DLSSG_Parameter_MultiFrameCountMax, &multiMax);
    NVSDK_NGX_D3D12_DestroyParameters(caps);

    DWORD hags = 0, size = sizeof(hags);
    bool hagsKnown = RegGetValueW(HKEY_LOCAL_MACHINE, L"SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers", L"HwSchMode", RRF_RT_REG_DWORD, nullptr, &hags, &size) == ERROR_SUCCESS;
    Log(1, std::format("available={} needsDriver={} minDriver={}.{} initResult={} multiFrameCountMax={} HAGS={}",
        available, needsDriver, major, minor, Hex((uint32_t)initResult), multiMax, hagsKnown ? (hags == 2 ? "on" : "off") : "default"));
    uint32_t limit = multiMax < 1 ? 1 : multiMax;
    if (frames > limit)
    {
        Log(2, std::format("the runtime allows {} generated frame(s) per real frame; using {}", limit, limit));
        frames = limit;
    }
    if (!available)
        throw std::runtime_error(needsDriver ? std::format("needs NVIDIA driver {}.{} or newer", major, minor)
                                             : std::format("not available on this GPU (init result {})", Hex((uint32_t)initResult)));

    r = NVSDK_NGX_D3D12_AllocateParameters(&params);
    if (r != NVSDK_NGX_Result_Success || !params) throw std::runtime_error(std::format("NGX AllocateParameters failed {}", Hex(r)));
    params->Set(NVSDK_NGX_DLSSG_Parameter_ResourceAlwaysProvided_Flags,
        (unsigned int)(NVSDK_NGX_DLSSG_ResourceFlags_Backbuffer | NVSDK_NGX_DLSSG_ResourceFlags_MVecs | NVSDK_NGX_DLSSG_ResourceFlags_Depth));
    params->Set(NVSDK_NGX_DLSSG_Parameter_ResourceNeverProvided_Flags,
        (unsigned int)(NVSDK_NGX_DLSSG_ResourceFlags_HUDLess | NVSDK_NGX_DLSSG_ResourceFlags_UI | NVSDK_NGX_DLSSG_ResourceFlags_UIAlpha | NVSDK_NGX_DLSSG_ResourceFlags_BidirectionalDistortionField));

    NVSDK_NGX_DLSSG_Create_Params cp{};
    cp.Width = w;
    cp.Height = h;
    cp.NativeBackbufferFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
    cp.RenderWidth = w;
    cp.RenderHeight = h;
    auto* l = Begin();
    r = SafeCreate(l, &fg, params, &cp, seh);
    Submit();
    Idle();
    if (r != NVSDK_NGX_Result_Success || !fg)
    {
        unsigned int fir = 0;
        params->Get(NVSDK_NGX_Parameter_FrameGeneration_FeatureInitResult, &fir);
        throw std::runtime_error(std::format("create failed {} (feature init {}, exception {})", Hex(r), Hex(fir), Hex(seh)));
    }
    LogRuntime();
}

void TngxSession::LogRuntime()
{
    HMODULE module = GetModuleHandleW(L"nvngx_dlssg.dll");
    wchar_t path[MAX_PATH]{};
    if (!module || !GetModuleFileNameW(module, path, MAX_PATH)) return;
    DWORD handle = 0, size = GetFileVersionInfoSizeW(path, &handle);
    std::vector<uint8_t> info(size);
    VS_FIXEDFILEINFO* v = nullptr;
    UINT len = 0;
    std::string version = "?";
    if (size && GetFileVersionInfoW(path, 0, size, info.data()) && VerQueryValueW(info.data(), L"\\", (void**)&v, &len) && v)
        version = std::format("{}.{}.{}", HIWORD(v->dwFileVersionMS), LOWORD(v->dwFileVersionMS), HIWORD(v->dwFileVersionLS));
    char narrow[MAX_PATH]{};
    WideCharToMultiByte(CP_UTF8, 0, path, -1, narrow, MAX_PATH, nullptr, nullptr);
    Log(1, std::format("runtime {} from {}", version, narrow));
}

bool TngxSession::Evaluate(uint64_t inputReady, bool reset, uint64_t& outputReady)
{
    queue->Wait(shared.Get(), inputReady);
    if (failed) return Fail(inputReady, outputReady);

    if (frameId > 1)
    {
        uint64_t* t = nullptr;
        D3D12_RANGE range{ 0, 2 * sizeof(uint64_t) };
        if (SUCCEEDED(timestampReadback->Map(0, &range, (void**)&t)))
        {
            if (t[1] > t[0] && gpuMs.size() < 100000) gpuMs.push_back((t[1] - t[0]) * 1000.0 / timestampFrequency);
            timestampReadback->Unmap(0, nullptr);
        }
    }
    auto* l = Begin();
    l->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0);
    backbuffer.To(l, D3D12_RESOURCE_STATE_COPY_DEST);
    l->CopyResource(backbuffer.res.Get(), input.Get());
    backbuffer.To(l, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    params->Set(NVSDK_NGX_DLSSG_Parameter_BackbufferFrameID, (unsigned long long)++frameId);

    for (uint32_t i = 0; i < frames; i++)
    {
    gen[i].To(l, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    NVSDK_NGX_D3D12_DLSSG_Eval_Params ep{};
    ep.pBackbuffer = backbuffer.res.Get();
    ep.pDepth = depth.res.Get();
    ep.pMVecs = mv.res.Get();
    ep.pOutputInterpFrame = gen[i].res.Get();

    // A still camera with a pinhole projection; the matrices must agree with each other even though motion is zero.
    NVSDK_NGX_DLSSG_Opt_Eval_Params op{};
    op.multiFrameCount = frames;
    op.multiFrameIndex = i + 1;
    float aspect = (float)w / h, nearZ = 0.1f, farZ = 1000.0f;
    float px = 1.0f / aspect, pz = farZ / (farZ - nearZ), pw = -nearZ * pz;
    for (int i = 0; i < 4; i++)
    {
        op.cameraViewToClip[i][i] = 1;
        op.clipToCameraView[i][i] = 1;
        op.clipToLensClip[i][i] = 1;
        op.clipToPrevClip[i][i] = 1;
        op.prevClipToClip[i][i] = 1;
    }
    op.cameraViewToClip[0][0] = px;
    op.cameraViewToClip[2][2] = pz;
    op.cameraViewToClip[2][3] = 1;
    op.cameraViewToClip[3][2] = pw;
    op.cameraViewToClip[3][3] = 0;
    op.clipToCameraView[0][0] = 1 / px;
    op.clipToCameraView[2][2] = 0;
    op.clipToCameraView[2][3] = 1 / pw;
    op.clipToCameraView[3][2] = 1;
    op.clipToCameraView[3][3] = -pz / pw;
    op.mvecScale[0] = 1.0f / w;
    op.mvecScale[1] = 1.0f / h;
    op.cameraNear = nearZ;
    op.cameraFar = farZ;
    op.cameraFOV = 1.5707963f;
    op.cameraAspectRatio = aspect;
    op.cameraUp[1] = 1;
    op.cameraRight[0] = 1;
    op.cameraFwd[2] = 1;
    op.cameraMotionIncluded = 1;
    op.reset = reset ? 1 : 0;
    op.motionVectorsInvalidValue = 3.402823466e38f;
    op.backbufferSubrectSize = { w, h };
    op.outputInterpSubrectSize = { w, h };
    op.mvecsSubrectSize = { w, h };
    op.depthSubrectSize = { w, h };

    DWORD seh = 0;
    NVSDK_NGX_Result r = SafeEvaluate(l, fg, params, &ep, &op, seh);
    if (r != NVSDK_NGX_Result_Success)
    {
        Log(2, std::format("evaluate {} of {} failed {} (exception {}) at frame {}", i + 1, frames, Hex(r), Hex(seh), frameId));
        l->Close();
        return Fail(inputReady, outputReady);
    }
    gen[i].To(l, D3D12_RESOURCE_STATE_COPY_SOURCE);
    l->CopyResource(output[i].Get(), gen[i].res.Get());
    }
    l->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 1);
    l->ResolveQueryData(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0, 2, timestampReadback.Get(), 0);
    Submit();
    queue->Signal(shared.Get(), inputReady + 1);
    outputReady = inputReady + 1;
    return true;
}

TngxSession::~TngxSession()
{
    Idle();
    if (gpuMs.size() > 10)
    {
        std::sort(gpuMs.begin(), gpuMs.end());
        Log(1, std::format("GPU time per real frame ({} generated): p50 {:.2f} ms, p95 {:.2f} ms, over {} frames",
            frames, gpuMs[gpuMs.size() / 2], gpuMs[gpuMs.size() * 95 / 100], gpuMs.size()));
    }
    if (fg) NVSDK_NGX_D3D12_ReleaseFeature(fg);
    if (params) NVSDK_NGX_D3D12_DestroyParameters(params);
    if (ngxInit) NVSDK_NGX_D3D12_Shutdown1(dev.Get());
    if (unlocked) MfgUnlock::Release();
    if (runtime) FreeLibrary(runtime);
    if (event) CloseHandle(event);
}

extern "C" __declspec(dllexport) int __cdecl TngxCreate(const TngxDesc* desc, TngxSession** session, TngxShared* shared)
{
    g_log = desc->log;
    *session = nullptr;
    *shared = {};
    auto* s = new TngxSession();
    try
    {
        s->Create(*desc, *shared);
        *session = s;
        return 0;
    }
    catch (const std::exception& e)
    {
        Log(2, e.what());
        for (HANDLE hnd : { shared->input, shared->output[0], shared->output[1], shared->output[2], shared->fence })
            if (hnd) CloseHandle(hnd);
        *shared = {};
        delete s;
        return 1;
    }
}

extern "C" __declspec(dllexport) int __cdecl TngxEvaluate(TngxSession* session, uint64_t inputReady, int reset, uint64_t* outputReady)
{
    return session->Evaluate(inputReady, reset != 0, *outputReady) ? 0 : 1;
}

extern "C" __declspec(dllexport) void __cdecl TngxDestroy(TngxSession* session)
{
    delete session;
}
