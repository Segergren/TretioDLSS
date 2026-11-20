// Native side of the GameStream host: ENet control channel, Reed-Solomon FEC, NVENC and DNS-SD.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <windns.h>
#include <d3d11.h>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

extern "C" {
#include <enet/enet.h>
#include "rs.h"
}
#include "nvEncodeAPI.h"

#define TS_API extern "C" __declspec(dllexport)

static void SetError(char* err, int errLen, const char* fmt, ...)
{
    if (!err || errLen <= 0) return;
    va_list args;
    va_start(args, fmt);
    vsnprintf(err, errLen, fmt, args);
    va_end(args);
}

// ---- ENet ----

TS_API int ts_enet_init() { return enet_initialize(); }

TS_API void* ts_enet_host_create(uint16_t port, int peers)
{
    ENetAddress addr{};
    enet_address_set_host(&addr, "0.0.0.0");
    enet_address_set_port(&addr, port);
    ENetHost* host = enet_host_create(AF_INET, &addr, (size_t)peers, 0, 0, 0);
    if (host) enet_socket_set_option(host->socket, ENET_SOCKOPT_QOS, 1);
    return host;
}

TS_API void ts_enet_host_destroy(void* host)
{
    if (host) enet_host_destroy((ENetHost*)host);
}

// Returns the event type (0 none, 1 connect, 2 disconnect, 3 receive) or a negative error.
// Received data is copied into buf (truncated to bufLen) and the packet is released.
TS_API int ts_enet_service(void* host, uint32_t timeoutMs, void** peer, uint32_t* data, uint8_t* buf, int bufLen, int* len)
{
    ENetEvent ev{};
    int r = enet_host_service((ENetHost*)host, &ev, timeoutMs);
    *len = 0;
    *peer = nullptr;
    *data = 0;
    if (r <= 0) return r;
    *peer = ev.peer;
    *data = ev.data;
    if (ev.type == ENET_EVENT_TYPE_RECEIVE && ev.packet)
    {
        int n = (int)ev.packet->dataLength;
        if (n > bufLen) n = bufLen;
        memcpy(buf, ev.packet->data, n);
        *len = n;
        enet_packet_destroy(ev.packet);
    }
    return (int)ev.type;
}

TS_API int ts_enet_send(void* host, void* peer, uint8_t channel, const uint8_t* data, int len, int reliable)
{
    ENetPacket* p = enet_packet_create(data, (size_t)len, reliable ? ENET_PACKET_FLAG_RELIABLE : 0);
    if (!p) return -1;
    ENetPeer* pe = (ENetPeer*)peer;
    if (channel >= pe->channelCount) channel = 0;
    int r = enet_peer_send(pe, channel, p);
    if (r != 0)
    {
        enet_packet_destroy(p);
        return r;
    }
    enet_host_flush((ENetHost*)host);
    return 0;
}

TS_API void ts_enet_disconnect(void* host, void* peer)
{
    enet_peer_disconnect_now((ENetPeer*)peer, 0);
    enet_host_flush((ENetHost*)host);
}

TS_API int ts_enet_peer_address(void* peer, char* buf, int bufLen)
{
    ENetPeer* p = (ENetPeer*)peer;
    auto* sa = (sockaddr*)&p->address.address;
    void* src = sa->sa_family == AF_INET ? (void*)&((sockaddr_in*)sa)->sin_addr : (void*)&((sockaddr_in6*)sa)->sin6_addr;
    return inet_ntop(sa->sa_family, src, buf, bufLen) ? 0 : -1;
}

// ---- Reed-Solomon ----

TS_API void ts_rs_init() { reed_solomon_init(); }

TS_API void* ts_rs_new(int dataShards, int parityShards) { return reed_solomon_new(dataShards, parityShards); }

TS_API void ts_rs_release(void* rs)
{
    if (rs) reed_solomon_release((reed_solomon*)rs);
}

TS_API void ts_rs_set_parity(void* rs, const uint8_t* p, int len) { memcpy(((reed_solomon*)rs)->p, p, len); }

TS_API int ts_rs_encode(void* rs, uint8_t** shards, int nrShards, int blockSize)
{
    return reed_solomon_encode((reed_solomon*)rs, shards, nrShards, blockSize);
}

// ---- DNS-SD (Windows' own mDNS responder) ----

struct DnsSd
{
    std::wstring name, host;
    PWCHAR keys[1] = {nullptr};
    PWCHAR values[1] = {nullptr};
    DNS_SERVICE_INSTANCE instance{};
    DNS_SERVICE_REGISTER_REQUEST req{};
    HANDLE done = nullptr;
    DWORD status = 0;
};

static VOID WINAPI DnsCompleted(DWORD status, PVOID ctx, PDNS_SERVICE_INSTANCE instance)
{
    auto* d = (DnsSd*)ctx;
    d->status = status;
    if (instance) DnsServiceFreeInstance(instance);
    SetEvent(d->done);
}

// instanceName is the full "<name>._nvstream._tcp.local"; hostName is "<computer>.local".
TS_API void* ts_mdns_register(const wchar_t* instanceName, const wchar_t* hostName, uint16_t port, char* err, int errLen)
{
    auto* d = new DnsSd();
    d->name = instanceName;
    d->host = hostName;
    d->done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    d->instance.pszInstanceName = d->name.data();
    d->instance.pszHostName = d->host.data();
    d->instance.wPort = port;
    d->instance.dwPropertyCount = 1;
    d->instance.keys = d->keys;
    d->instance.values = d->values;
    d->req.Version = DNS_QUERY_REQUEST_VERSION1;
    d->req.pServiceInstance = &d->instance;
    d->req.pRegisterCompletionCallback = DnsCompleted;
    d->req.pQueryContext = d;
    DWORD r = DnsServiceRegister(&d->req, nullptr);
    if (r != DNS_REQUEST_PENDING)
    {
        SetError(err, errLen, "DnsServiceRegister failed (%lu)", r);
        CloseHandle(d->done);
        delete d;
        return nullptr;
    }
    if (WaitForSingleObject(d->done, 5000) != WAIT_OBJECT_0 || d->status != ERROR_SUCCESS)
        SetError(err, errLen, "DNS-SD registration status %lu", d->status);
    return d;
}

TS_API void ts_mdns_deregister(void* handle)
{
    auto* d = (DnsSd*)handle;
    if (!d) return;
    if (DnsServiceDeRegister(&d->req, nullptr) == DNS_REQUEST_PENDING) WaitForSingleObject(d->done, 3000);
    CloseHandle(d->done);
    delete d;
}

// ---- NVENC ----

typedef NVENCSTATUS(NVENCAPI* PfnCreateInstance)(NV_ENCODE_API_FUNCTION_LIST*);
typedef NVENCSTATUS(NVENCAPI* PfnMaxVersion)(uint32_t*);

struct Slot
{
    ID3D11Texture2D* texture = nullptr;
    NV_ENC_REGISTERED_PTR registered = nullptr;
    NV_ENC_INPUT_PTR mapped = nullptr;
    NV_ENC_OUTPUT_PTR bitstream = nullptr;
    HANDLE event = nullptr;
    bool busy = false;
    bool locked = false;
};

struct Encoder
{
    HMODULE lib = nullptr;
    NV_ENCODE_API_FUNCTION_LIST fn{};
    void* session = nullptr;
    std::vector<Slot> slots;
    std::mutex gate;
    int width = 0, height = 0;
};

static void DestroyEncoder(Encoder* e)
{
    if (!e) return;
    if (e->session)
    {
        for (auto& s : e->slots)
        {
            if (s.locked) e->fn.nvEncUnlockBitstream(e->session, s.bitstream);
            if (s.mapped) e->fn.nvEncUnmapInputResource(e->session, s.mapped);
            if (s.registered) e->fn.nvEncUnregisterResource(e->session, s.registered);
            if (s.bitstream) e->fn.nvEncDestroyBitstreamBuffer(e->session, s.bitstream);
            if (s.event)
            {
                NV_ENC_EVENT_PARAMS ep{};
                ep.version = NV_ENC_EVENT_PARAMS_VER;
                ep.completionEvent = s.event;
                e->fn.nvEncUnregisterAsyncEvent(e->session, &ep);
            }
        }
        e->fn.nvEncDestroyEncoder(e->session);
    }
    for (auto& s : e->slots)
    {
        if (s.event) CloseHandle(s.event);
        if (s.texture) s.texture->Release();
    }
    if (e->lib) FreeLibrary(e->lib);
    delete e;
}

static GUID CodecGuid(int codec) { return codec == 2 ? NV_ENC_CODEC_AV1_GUID : codec == 1 ? NV_ENC_CODEC_HEVC_GUID : NV_ENC_CODEC_H264_GUID; }

// codec: 0 H.264, 1 HEVC, 2 AV1. Creates `ring` NV12 textures (render-target bindable) owned by the encoder.
TS_API void* ts_nvenc_create(ID3D11Device* device, int codec, int width, int height, int fps, int kbps, int ring, char* err, int errLen)
{
    auto* e = new Encoder();
    e->width = width;
    e->height = height;
    e->lib = LoadLibraryW(L"nvEncodeAPI64.dll");
    if (!e->lib)
    {
        SetError(err, errLen, "nvEncodeAPI64.dll not found (NVIDIA driver missing?)");
        DestroyEncoder(e);
        return nullptr;
    }
    auto maxVersion = (PfnMaxVersion)GetProcAddress(e->lib, "NvEncodeAPIGetMaxSupportedVersion");
    auto create = (PfnCreateInstance)GetProcAddress(e->lib, "NvEncodeAPICreateInstance");
    uint32_t driverVersion = 0;
    if (!maxVersion || !create || maxVersion(&driverVersion) != NV_ENC_SUCCESS)
    {
        SetError(err, errLen, "NVENC entry points missing");
        DestroyEncoder(e);
        return nullptr;
    }
    uint32_t needed = (NVENCAPI_MAJOR_VERSION << 4) | NVENCAPI_MINOR_VERSION;
    if (driverVersion < needed)
    {
        SetError(err, errLen, "Driver NVENC API %u.%u is older than %u.%u; update the NVIDIA driver", driverVersion >> 4, driverVersion & 0xF, NVENCAPI_MAJOR_VERSION, NVENCAPI_MINOR_VERSION);
        DestroyEncoder(e);
        return nullptr;
    }
    e->fn.version = NV_ENCODE_API_FUNCTION_LIST_VER;
    if (create(&e->fn) != NV_ENC_SUCCESS)
    {
        SetError(err, errLen, "NvEncodeAPICreateInstance failed");
        DestroyEncoder(e);
        return nullptr;
    }

    NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open{};
    open.version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
    open.device = device;
    open.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX;
    open.apiVersion = NVENCAPI_VERSION;
    NVENCSTATUS st = e->fn.nvEncOpenEncodeSessionEx(&open, &e->session);
    if (st != NV_ENC_SUCCESS)
    {
        SetError(err, errLen, "nvEncOpenEncodeSessionEx failed (%d)", (int)st);
        e->session = nullptr;
        DestroyEncoder(e);
        return nullptr;
    }

    GUID codecGuid = CodecGuid(codec);
    NV_ENC_PRESET_CONFIG preset{};
    preset.version = NV_ENC_PRESET_CONFIG_VER;
    preset.presetCfg.version = NV_ENC_CONFIG_VER;
    st = e->fn.nvEncGetEncodePresetConfigEx(e->session, codecGuid, NV_ENC_PRESET_P1_GUID, NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY, &preset);
    if (st != NV_ENC_SUCCESS)
    {
        SetError(err, errLen, "nvEncGetEncodePresetConfigEx failed (%d), codec %d not supported?", (int)st, codec);
        DestroyEncoder(e);
        return nullptr;
    }

    NV_ENC_CONFIG cfg = preset.presetCfg;
    cfg.version = NV_ENC_CONFIG_VER;
    cfg.gopLength = NVENC_INFINITE_GOPLENGTH;
    cfg.frameIntervalP = 1;
    cfg.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CBR;
    cfg.rcParams.multiPass = NV_ENC_MULTI_PASS_DISABLED;
    cfg.rcParams.averageBitRate = (uint32_t)kbps * 1000u;
    cfg.rcParams.maxBitRate = cfg.rcParams.averageBitRate;
    cfg.rcParams.vbvBufferSize = (uint32_t)((uint64_t)cfg.rcParams.averageBitRate / (uint32_t)fps);
    cfg.rcParams.vbvInitialDelay = cfg.rcParams.vbvBufferSize;
    cfg.rcParams.zeroReorderDelay = 1;
    cfg.rcParams.enableAQ = 0;
    cfg.rcParams.enableLookahead = 0;
    if (codec == 0)
    {
        auto& c = cfg.encodeCodecConfig.h264Config;
        cfg.profileGUID = NV_ENC_H264_PROFILE_HIGH_GUID;
        c.idrPeriod = NVENC_INFINITE_GOPLENGTH;
        c.repeatSPSPPS = 1;
        c.sliceMode = 3;
        c.sliceModeData = 1;
        c.chromaFormatIDC = 1;
        c.maxNumRefFrames = 1;
        auto& v = c.h264VUIParameters;
        v.videoSignalTypePresentFlag = 1;
        v.videoFormat = NV_ENC_VUI_VIDEO_FORMAT_UNSPECIFIED;
        v.videoFullRangeFlag = 0;
        v.colourDescriptionPresentFlag = 1;
        v.colourPrimaries = NV_ENC_VUI_COLOR_PRIMARIES_BT709;
        v.transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709;
        v.colourMatrix = NV_ENC_VUI_MATRIX_COEFFS_BT709;
    }
    else if (codec == 1)
    {
        auto& c = cfg.encodeCodecConfig.hevcConfig;
        cfg.profileGUID = NV_ENC_HEVC_PROFILE_MAIN_GUID;
        c.idrPeriod = NVENC_INFINITE_GOPLENGTH;
        c.repeatSPSPPS = 1;
        c.sliceMode = 3;
        c.sliceModeData = 1;
        c.chromaFormatIDC = 1;
        c.maxNumRefFramesInDPB = 1;
        auto& v = c.hevcVUIParameters;
        v.videoSignalTypePresentFlag = 1;
        v.videoFormat = NV_ENC_VUI_VIDEO_FORMAT_UNSPECIFIED;
        v.videoFullRangeFlag = 0;
        v.colourDescriptionPresentFlag = 1;
        v.colourPrimaries = NV_ENC_VUI_COLOR_PRIMARIES_BT709;
        v.transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709;
        v.colourMatrix = NV_ENC_VUI_MATRIX_COEFFS_BT709;
    }
    else
    {
        auto& c = cfg.encodeCodecConfig.av1Config;
        cfg.profileGUID = NV_ENC_AV1_PROFILE_MAIN_GUID;
        c.idrPeriod = NVENC_INFINITE_GOPLENGTH;
        c.repeatSeqHdr = 1;
        c.chromaFormatIDC = 1;
        c.colorPrimaries = NV_ENC_VUI_COLOR_PRIMARIES_BT709;
        c.transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709;
        c.matrixCoefficients = NV_ENC_VUI_MATRIX_COEFFS_BT709;
        c.colorRange = 0;
    }

    NV_ENC_INITIALIZE_PARAMS init{};
    init.version = NV_ENC_INITIALIZE_PARAMS_VER;
    init.encodeGUID = codecGuid;
    init.presetGUID = NV_ENC_PRESET_P1_GUID;
    init.tuningInfo = NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;
    init.encodeWidth = width;
    init.encodeHeight = height;
    init.darWidth = width;
    init.darHeight = height;
    init.frameRateNum = fps;
    init.frameRateDen = 1;
    init.enablePTD = 1;
    init.enableEncodeAsync = 1;
    init.encodeConfig = &cfg;
    st = e->fn.nvEncInitializeEncoder(e->session, &init);
    if (st != NV_ENC_SUCCESS)
    {
        SetError(err, errLen, "nvEncInitializeEncoder failed (%d) for %dx%d@%d, codec %d", (int)st, width, height, fps, codec);
        DestroyEncoder(e);
        return nullptr;
    }

    e->slots.resize(ring);
    for (auto& s : e->slots)
    {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = width;
        desc.Height = height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_NV12;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        HRESULT hr = device->CreateTexture2D(&desc, nullptr, &s.texture);
        if (FAILED(hr))
        {
            SetError(err, errLen, "NV12 texture creation failed (0x%08lx)", (unsigned long)hr);
            DestroyEncoder(e);
            return nullptr;
        }
        NV_ENC_REGISTER_RESOURCE reg{};
        reg.version = NV_ENC_REGISTER_RESOURCE_VER;
        reg.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
        reg.width = width;
        reg.height = height;
        reg.resourceToRegister = s.texture;
        reg.bufferFormat = NV_ENC_BUFFER_FORMAT_NV12;
        reg.bufferUsage = NV_ENC_INPUT_IMAGE;
        st = e->fn.nvEncRegisterResource(e->session, &reg);
        if (st != NV_ENC_SUCCESS)
        {
            SetError(err, errLen, "nvEncRegisterResource failed (%d)", (int)st);
            DestroyEncoder(e);
            return nullptr;
        }
        s.registered = reg.registeredResource;
        NV_ENC_CREATE_BITSTREAM_BUFFER bb{};
        bb.version = NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
        st = e->fn.nvEncCreateBitstreamBuffer(e->session, &bb);
        if (st != NV_ENC_SUCCESS)
        {
            SetError(err, errLen, "nvEncCreateBitstreamBuffer failed (%d)", (int)st);
            DestroyEncoder(e);
            return nullptr;
        }
        s.bitstream = bb.bitstreamBuffer;
        s.event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        NV_ENC_EVENT_PARAMS ep{};
        ep.version = NV_ENC_EVENT_PARAMS_VER;
        ep.completionEvent = s.event;
        e->fn.nvEncRegisterAsyncEvent(e->session, &ep);
    }
    return e;
}

TS_API ID3D11Texture2D* ts_nvenc_texture(void* enc, int slot) { return ((Encoder*)enc)->slots[slot].texture; }

TS_API int ts_nvenc_slot_busy(void* enc, int slot)
{
    auto* e = (Encoder*)enc;
    std::lock_guard<std::mutex> lock(e->gate);
    return e->slots[slot].busy ? 1 : 0;
}

// Pipeline thread, right after drawing into the slot's texture.
TS_API int ts_nvenc_submit(void* enc, int slot, int forceIdr, uint64_t frameIndex)
{
    auto* e = (Encoder*)enc;
    std::lock_guard<std::mutex> lock(e->gate);
    Slot& s = e->slots[slot];
    if (s.busy) return -100;
    NV_ENC_MAP_INPUT_RESOURCE map{};
    map.version = NV_ENC_MAP_INPUT_RESOURCE_VER;
    map.registeredResource = s.registered;
    NVENCSTATUS st = e->fn.nvEncMapInputResource(e->session, &map);
    if (st != NV_ENC_SUCCESS) return -(int)st;
    s.mapped = map.mappedResource;
    NV_ENC_PIC_PARAMS pic{};
    pic.version = NV_ENC_PIC_PARAMS_VER;
    pic.inputWidth = e->width;
    pic.inputHeight = e->height;
    pic.inputPitch = e->width;
    pic.inputBuffer = s.mapped;
    pic.outputBitstream = s.bitstream;
    pic.completionEvent = s.event;
    pic.bufferFmt = map.mappedBufferFmt;
    pic.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
    pic.inputTimeStamp = frameIndex;
    pic.encodePicFlags = forceIdr ? (NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS) : 0;
    st = e->fn.nvEncEncodePicture(e->session, &pic);
    if (st != NV_ENC_SUCCESS && st != NV_ENC_ERR_NEED_MORE_INPUT)
    {
        e->fn.nvEncUnmapInputResource(e->session, s.mapped);
        s.mapped = nullptr;
        return -(int)st;
    }
    s.busy = true;
    return 0;
}

// Sender thread: waits for the slot's encode to finish and returns the bitstream (valid until ts_nvenc_release).
TS_API int ts_nvenc_collect(void* enc, int slot, uint32_t timeoutMs, const uint8_t** data, int* len, int* idr)
{
    auto* e = (Encoder*)enc;
    Slot& s = e->slots[slot];
    if (WaitForSingleObject(s.event, timeoutMs) != WAIT_OBJECT_0) return -101;
    std::lock_guard<std::mutex> lock(e->gate);
    NV_ENC_LOCK_BITSTREAM lb{};
    lb.version = NV_ENC_LOCK_BITSTREAM_VER;
    lb.outputBitstream = s.bitstream;
    NVENCSTATUS st = e->fn.nvEncLockBitstream(e->session, &lb);
    if (st != NV_ENC_SUCCESS) return -(int)st;
    s.locked = true;
    *data = (const uint8_t*)lb.bitstreamBufferPtr;
    *len = (int)lb.bitstreamSizeInBytes;
    *idr = (lb.pictureType == NV_ENC_PIC_TYPE_IDR || lb.pictureType == NV_ENC_PIC_TYPE_I) ? 1 : 0;
    return 0;
}

TS_API void ts_nvenc_release(void* enc, int slot)
{
    auto* e = (Encoder*)enc;
    std::lock_guard<std::mutex> lock(e->gate);
    Slot& s = e->slots[slot];
    if (s.locked) e->fn.nvEncUnlockBitstream(e->session, s.bitstream);
    s.locked = false;
    if (s.mapped) e->fn.nvEncUnmapInputResource(e->session, s.mapped);
    s.mapped = nullptr;
    s.busy = false;
}

TS_API void ts_nvenc_destroy(void* enc) { DestroyEncoder((Encoder*)enc); }
