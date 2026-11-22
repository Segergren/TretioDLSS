// TretioNr.nvngx.dll: makes the calls into nvngx_dlssnr.dll for TretioNgx.dll.
// The runtime looks up the module of its caller's return address and accepts only a path containing "nvngx.dll".
// Each call stores its result in a volatile so it is never turned into a tail jump.

#include <windows.h>
#include <d3d12.h>

typedef int(__cdecl* InitFn)(unsigned long long, const wchar_t*, ID3D12Device*, int, const void*);
typedef int(__cdecl* CreateFn)(ID3D12GraphicsCommandList*, int, void*, void**);
typedef int(__cdecl* EvaluateFn)(ID3D12GraphicsCommandList*, const void*, const void*, void*);
typedef int(__cdecl* ReleaseFn)(void*);
typedef int(__cdecl* ShutdownFn)(ID3D12Device*);

constexpr int kCrashed = (int)0xBAD000FF;

extern "C" __declspec(dllexport) int __cdecl TnrCallInit(InitFn fn, unsigned long long appId, const wchar_t* dataDir, ID3D12Device* device, int version, const void* params, DWORD* seh)
{
    *seh = 0;
    __try { volatile int r = fn(appId, dataDir, device, version, params); return r; }
    __except (EXCEPTION_EXECUTE_HANDLER) { *seh = GetExceptionCode(); return kCrashed; }
}

extern "C" __declspec(dllexport) int __cdecl TnrCallCreate(CreateFn fn, ID3D12GraphicsCommandList* list, void* params, void** handle, DWORD* seh)
{
    *seh = 0;
    __try { volatile int r = fn(list, 18, params, handle); return r; }
    __except (EXCEPTION_EXECUTE_HANDLER) { *seh = GetExceptionCode(); return kCrashed; }
}

extern "C" __declspec(dllexport) int __cdecl TnrCallEvaluate(EvaluateFn fn, ID3D12GraphicsCommandList* list, const void* handle, const void* params, DWORD* seh)
{
    *seh = 0;
    __try { volatile int r = fn(list, handle, params, nullptr); return r; }
    __except (EXCEPTION_EXECUTE_HANDLER) { *seh = GetExceptionCode(); return kCrashed; }
}

extern "C" __declspec(dllexport) int __cdecl TnrCallRelease(ReleaseFn fn, void* handle, DWORD* seh)
{
    *seh = 0;
    __try { volatile int r = fn(handle); return r; }
    __except (EXCEPTION_EXECUTE_HANDLER) { *seh = GetExceptionCode(); return kCrashed; }
}

extern "C" __declspec(dllexport) int __cdecl TnrCallShutdown(ShutdownFn fn, ID3D12Device* device, DWORD* seh)
{
    *seh = 0;
    __try { volatile int r = fn(device); return r; }
    __except (EXCEPTION_EXECUTE_HANDLER) { *seh = GetExceptionCode(); return kCrashed; }
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
