#pragma once

#include <windows.h>
#include <d3d12.h>
#include <wrl/client.h>

#include <cstdint>
#include <format>
#include <string>

typedef void(__cdecl* TngxLog)(int level, const char* message);

inline std::string Hex(uint64_t v) { return std::format("0x{:X}", v); }

// A D3D12 resource and the state it was last moved to.
struct Tracked
{
    Microsoft::WRL::ComPtr<ID3D12Resource> res;
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
