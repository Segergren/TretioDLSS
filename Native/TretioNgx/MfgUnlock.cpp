#include "MfgUnlock.h"

#include <bcrypt.h>

#include <array>
#include <cstdint>
#include <cstring>
#include <format>
#include <mutex>
#include <vector>

namespace {

// Identity of the runtime every pattern below was verified against (NVIDIA/DLSS v310.7.0).
constexpr uint32_t kTimeDateStamp = 0x69FB633Cu;
constexpr uint32_t kSizeOfImage = 0x00745000u;

// Arch gates: compares of the NVAPI architecture id against 0x1B0 (Blackwell).
constexpr size_t kMinArchGates = 1, kMaxArchGates = 4;

// Count validator: test dl,dl ; jz <cmp r8d,1> ; mov esi,5. The jz becomes jmp +4.
constexpr uint8_t kCountGate[] = { 0x84, 0xd2, 0x0f, 0x84, 0x03, 0x01, 0x00, 0x00, 0xbe, 0x05, 0x00, 0x00, 0x00 };
constexpr uint8_t kCountGateOriginal[2] = { 0x0f, 0x84 };
constexpr uint8_t kCountGateReplacement[2] = { 0xeb, 0x04 };
constexpr uint8_t kCountGateTarget[4] = { 0x41, 0x83, 0xf8, 0x01 };

// Interpolation kernel: the sm_89 PTX blends at a fixed 0.5. It is rebuilt to use the kernel's time parameter.
constexpr uint32_t kFatbinMagic = 0xBA55ED50u;
constexpr size_t kOuterHeader = 16;
constexpr uint32_t kPtxKind = 1;
constexpr uint64_t kUncompressedFlags = 0x41;
constexpr size_t kEntryNameOffset = 0x10, kDescriptorNameOffset = 0x28;
constexpr char kDescriptorName[] = "dlfg_kernel";
constexpr char kEntryName[] = "main_kernel";
constexpr size_t kSm89Entry = kOuterHeader + 104 + 28024;
constexpr uint64_t kSourceFatbinBytes = 98408, kPtxBytes = 99362, kRebuiltFatbinBytes = 127200;
constexpr size_t kMidpoints = 104;
constexpr char kSourceFatbinSha[] = "5A8E0284AAB8AC14FC82B0504BBEEF25D2FCE1D13A1C11D8BDB3F91FEE8145FC";
constexpr char kSourcePtxSha[] = "46C05996A2EF199BBE39378681734ED5EE757655DE70410D9900D85BA91222F1";
constexpr char kRebuiltFatbinSha[] = "19FB3CD5500BFD1FC88F96C36B381E096D6AB1DFC7B1DB02A04D40A00849104B";
constexpr char kJoinLabel[] = "$L__BB0_3:";
constexpr char kMidpointBits[] = "0f3F000000";
constexpr char kMulPrefix[] = "mul.ftz.f32 ";
constexpr char kCurrToPrev[] = "%f136";
constexpr char kPrevToCurr[] = "%f134";
constexpr char kTemporalInput[] =
    "ld.param.f32 %f134, [main_kernel_param_0+32];\r\n"
    "mov.f32 %f135, 0f3F800000;\r\n"
    "sub.ftz.f32 %f136, %f135, %f134;\r\n";

uint16_t U16(const uint8_t* p) { uint16_t v; memcpy(&v, p, 2); return v; }
uint32_t U32(const uint8_t* p) { uint32_t v; memcpy(&v, p, 4); return v; }
uint64_t U64(const uint8_t* p) { uint64_t v; memcpy(&v, p, 8); return v; }

bool Sha256Is(const uint8_t* bytes, size_t count, const char* expected)
{
    BCRYPT_ALG_HANDLE alg = nullptr;
    std::array<uint8_t, 32> digest{};
    NTSTATUS st = BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0);
    if (st >= 0) st = BCryptHash(alg, nullptr, 0, const_cast<PUCHAR>(bytes), (ULONG)count, digest.data(), (ULONG)digest.size());
    if (alg) BCryptCloseAlgorithmProvider(alg, 0);
    if (st < 0) return false;
    static constexpr char kDigits[] = "0123456789ABCDEF";
    for (size_t i = 0; i < digest.size(); i++)
        if (expected[i * 2] != kDigits[digest[i] >> 4] || expected[i * 2 + 1] != kDigits[digest[i] & 15]) return false;
    return true;
}

bool Lz4Decompress(const uint8_t* src, size_t srcSize, uint8_t* dst, size_t dstSize)
{
    size_t in = 0, out = 0;
    while (in < srcSize)
    {
        uint8_t token = src[in++];
        size_t literals = token >> 4;
        if (literals == 15)
        {
            uint8_t ext;
            do { if (in >= srcSize) return false; ext = src[in++]; literals += ext; } while (ext == 0xFF);
        }
        if (literals > srcSize - in || literals > dstSize - out) return false;
        memcpy(dst + out, src + in, literals);
        in += literals;
        out += literals;
        if (in == srcSize) break;
        if (srcSize - in < 2) return false;
        size_t back = size_t(src[in]) | (size_t(src[in + 1]) << 8);
        in += 2;
        if (back == 0 || back > out) return false;
        size_t match = 4 + (token & 15);
        if ((token & 15) == 15)
        {
            uint8_t ext;
            do { if (in >= srcSize) return false; ext = src[in++]; match += ext; } while (ext == 0xFF);
        }
        if (match > dstSize - out) return false;
        for (size_t i = 0; i < match; i++) dst[out + i] = dst[out + i - back];
        out += match;
    }
    return in == srcSize && out == dstSize;
}

bool Write(void* at, const void* bytes, size_t count)
{
    DWORD old = 0;
    if (!VirtualProtect(at, count, PAGE_EXECUTE_READWRITE, &old)) return false;
    memcpy(at, bytes, count);
    FlushInstructionCache(GetCurrentProcess(), at, count);
    DWORD ignored = 0;
    VirtualProtect(at, count, old, &ignored);
    return memcmp(at, bytes, count) == 0;
}

struct Saved
{
    uint8_t* at;
    std::vector<uint8_t> bytes;
};

std::mutex g_mutex;
std::vector<Saved> g_saved;
void* g_kernel = nullptr;

bool Patch(void* at, const void* bytes, size_t count)
{
    g_saved.push_back({ (uint8_t*)at, std::vector<uint8_t>((uint8_t*)at, (uint8_t*)at + count) });
    return Write(at, bytes, count);
}

void RollBack()
{
    for (auto it = g_saved.rbegin(); it != g_saved.rend(); ++it) Write(it->at, it->bytes.data(), it->bytes.size());
    g_saved.clear();
    if (g_kernel) VirtualFree(g_kernel, 0, MEM_RELEASE);
    g_kernel = nullptr;
}

bool Readable(const void* p, size_t bytes);

template <typename F>
void ForEachSection(uint8_t* base, const IMAGE_NT_HEADERS64* nt, bool executable, F f)
{
    auto* s = IMAGE_FIRST_SECTION(nt);
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; i++, s++)
    {
        bool exec = (s->Characteristics & IMAGE_SCN_MEM_EXECUTE) != 0;
        if (exec != executable || !(s->Characteristics & IMAGE_SCN_MEM_READ)) continue;
        if (!Readable(base + s->VirtualAddress, s->Misc.VirtualSize)) continue;
        f(base + s->VirtualAddress, (size_t)s->Misc.VirtualSize);
    }
}

std::vector<uint8_t*> FindArchGates(uint8_t* base, const IMAGE_NT_HEADERS64* nt)
{
    std::vector<uint8_t*> sites;
    ForEachSection(base, nt, true, [&](uint8_t* p, size_t size) {
        for (size_t off = 0; off + 6 <= size; off++)
        {
            if (p[off] == 0x3D && p[off + 1] == 0xB0 && p[off + 2] == 0x01 && p[off + 3] == 0 && p[off + 4] == 0)
            {
                sites.push_back(p + off + 1);
                off += 4;
            }
            else if (p[off] == 0x81 && p[off + 1] >= 0xF8 && p[off + 2] == 0xB0 && p[off + 3] == 0x01 && p[off + 4] == 0 && p[off + 5] == 0)
            {
                sites.push_back(p + off + 2);
                off += 5;
            }
        }
    });
    return sites;
}

uint8_t* FindCountGate(uint8_t* base, const IMAGE_NT_HEADERS64* nt, size_t& matches)
{
    uint8_t* found = nullptr;
    matches = 0;
    ForEachSection(base, nt, true, [&](uint8_t* p, size_t size) {
        for (size_t off = 0; off + sizeof(kCountGate) <= size; off++)
            if (memcmp(p + off, kCountGate, sizeof(kCountGate)) == 0)
            {
                matches++;
                found = p + off;
            }
    });
    if (matches != 1) return nullptr;
    int32_t displacement;
    memcpy(&displacement, found + 4, 4);
    uint8_t* target = found + 8 + displacement;
    uint8_t* end = base + nt->OptionalHeader.SizeOfImage;
    if (target < base || target + 4 > end || memcmp(target, kCountGateTarget, 4) != 0) return nullptr;
    return found + 2;
}

bool Readable(const void* p, size_t bytes)
{
    auto at = (uintptr_t)p;
    auto end = at + bytes;
    while (at < end)
    {
        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery((const void*)at, &mbi, sizeof(mbi)) || mbi.State != MEM_COMMIT) return false;
        if (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) return false;
        at = (uintptr_t)mbi.BaseAddress + mbi.RegionSize;
    }
    return true;
}

bool PointsTo(uint8_t* base, size_t imageSize, uint64_t value, const char* text)
{
    auto start = (uintptr_t)base;
    size_t len = strlen(text);
    if (value < start || value >= start + imageSize || start + imageSize - value < len + 1 || !Readable((const void*)value, len + 1)) return false;
    return memcmp((const void*)value, text, len + 1) == 0;
}

std::vector<uint64_t*> FindKernelDescriptors(uint8_t* base, const IMAGE_NT_HEADERS64* nt, const uint8_t*& fatbin)
{
    std::vector<uint64_t*> slots;
    size_t imageSize = nt->OptionalHeader.SizeOfImage;
    auto start = (uintptr_t)base;
    ForEachSection(base, nt, false, [&](uint8_t* p, size_t size) {
        for (size_t off = 0; off + kDescriptorNameOffset + 8 <= size; off += 8)
        {
            uint64_t value = U64(p + off);
            if (value < start || value >= start + imageSize || start + imageSize - value < kSourceFatbinBytes) continue;
            auto* candidate = (const uint8_t*)value;
            if (!Readable(candidate, kOuterHeader) || U32(candidate) != kFatbinMagic) continue;
            if (!PointsTo(base, imageSize, U64(p + off + kEntryNameOffset), kEntryName)) continue;
            if (!PointsTo(base, imageSize, U64(p + off + kDescriptorNameOffset), kDescriptorName)) continue;
            if (U64(candidate + 8) + kOuterHeader != kSourceFatbinBytes || !Readable(candidate, kSourceFatbinBytes)) continue;
            slots.push_back((uint64_t*)(p + off));
            fatbin = candidate;
        }
    });
    return slots;
}

bool RebuildKernel(const uint8_t* fat, std::vector<uint8_t>& out, std::string& why)
{
    if (!Sha256Is(fat, kSourceFatbinBytes, kSourceFatbinSha)) { why = "kernel fatbin is not the verified build"; return false; }
    const uint8_t* e = fat + kSm89Entry;
    uint32_t hdr = U32(e + 4), compressed = U32(e + 16);
    if (U16(e) != kPtxKind || U32(e + 28) != 89 || U64(e + 56) != kPtxBytes) { why = "no sm_89 PTX entry where expected"; return false; }
    std::vector<uint8_t> ptx(kPtxBytes);
    if (!Lz4Decompress(e + hdr, compressed, ptx.data(), ptx.size())) { why = "PTX did not decompress"; return false; }
    if (!Sha256Is(ptx.data(), ptx.size(), kSourcePtxSha)) { why = "PTX is not the verified build"; return false; }

    const char* text = (const char*)ptx.data();
    size_t n = ptx.size(), labelLen = sizeof(kJoinLabel) - 1, midLen = sizeof(kMidpointBits) - 1, mulLen = sizeof(kMulPrefix) - 1;
    size_t label = SIZE_MAX;
    for (size_t i = 0; i + labelLen <= n; i++)
        if (memcmp(text + i, kJoinLabel, labelLen) == 0)
        {
            if (label != SIZE_MAX) { why = "join label is not unique"; return false; }
            label = i;
        }
    if (label == SIZE_MAX) { why = "join label not found"; return false; }
    size_t insertion = label + labelLen;
    while (insertion < n && text[insertion] != '\n') insertion++;
    insertion++;

    std::vector<size_t> marks;
    for (size_t i = 0; i + midLen < n; i++)
    {
        if (memcmp(text + i, kMidpointBits, midLen) != 0 || text[i + midLen] != ';') continue;
        size_t line = i;
        while (line > 0 && text[line - 1] != '\n') line--;
        if (i - line >= mulLen && memcmp(text + line, kMulPrefix, mulLen) == 0) marks.push_back(i);
    }
    if (marks.size() != kMidpoints || marks.front() <= insertion) { why = std::format("{} midpoint multiplies, expected {}", marks.size(), kMidpoints); return false; }

    std::vector<uint8_t> patched;
    auto append = [&](const void* p, size_t count) { patched.insert(patched.end(), (const uint8_t*)p, (const uint8_t*)p + count); };
    append(ptx.data(), insertion);
    append(kTemporalInput, sizeof(kTemporalInput) - 1);
    size_t from = insertion;
    for (size_t i = 0; i < marks.size(); i++)
    {
        append(ptx.data() + from, marks[i] - from);
        append(i < kMidpoints / 2 ? kCurrToPrev : kPrevToCurr, 5);
        from = marks[i] + midLen;
    }
    append(ptx.data() + from, n - from);

    size_t padded = (patched.size() + 7) & ~size_t(7);
    size_t total = kSm89Entry + hdr + padded;
    out.assign(fat, fat + kSm89Entry + hdr);
    out.resize(total, 0);
    memcpy(out.data() + kSm89Entry + hdr, patched.data(), patched.size());
    uint64_t payload = padded, zero64 = 0, outer = total - kOuterHeader;
    uint32_t zero32 = 0;
    memcpy(out.data() + kSm89Entry + 8, &payload, 8);
    memcpy(out.data() + kSm89Entry + 16, &zero32, 4);
    memcpy(out.data() + kSm89Entry + 40, &kUncompressedFlags, 4);
    memcpy(out.data() + kSm89Entry + 56, &zero64, 8);
    memcpy(out.data() + 8, &outer, 8);
    if (out.size() != kRebuiltFatbinBytes || !Sha256Is(out.data(), out.size(), kRebuiltFatbinSha)) { why = "rebuilt kernel does not match the verified result"; return false; }
    return true;
}

int FaultFilter(EXCEPTION_POINTERS* e, uintptr_t& at, uintptr_t& ip)
{
    ip = (uintptr_t)e->ExceptionRecord->ExceptionAddress;
    at = e->ExceptionRecord->NumberParameters > 1 ? e->ExceptionRecord->ExceptionInformation[1] : 0;
    return EXCEPTION_EXECUTE_HANDLER;
}

bool FindDescriptorsInto(uint8_t* base, const IMAGE_NT_HEADERS64* nt, const uint8_t*& fatbin, std::vector<uint64_t*>& out)
{
    out = FindKernelDescriptors(base, nt, fatbin);
    return true;
}

__declspec(noinline) bool SafeFindDescriptors(uint8_t* base, const IMAGE_NT_HEADERS64* nt, const uint8_t*& fatbin, std::vector<uint64_t*>& out, uintptr_t& at, uintptr_t& ip)
{
    __try { return FindDescriptorsInto(base, nt, fatbin, out); }
    __except (FaultFilter(GetExceptionInformation(), at, ip)) { return false; }
}

} // namespace

namespace MfgUnlock {

bool IsAda(UINT vendorId, UINT deviceId)
{
    return vendorId == 0x10DE && deviceId >= 0x2680 && deviceId <= 0x28FF;
}

bool Apply(HMODULE runtime, std::string& detail)
{
    std::lock_guard lock(g_mutex);
    if (!g_saved.empty()) { detail = "already applied"; return true; }
    auto* base = (uint8_t*)runtime;
    auto* dos = (const IMAGE_DOS_HEADER*)base;
    auto* nt = (const IMAGE_NT_HEADERS64*)(base + dos->e_lfanew);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || nt->Signature != IMAGE_NT_SIGNATURE) { detail = "runtime is not a PE image"; return false; }
    if (nt->FileHeader.TimeDateStamp != kTimeDateStamp || nt->OptionalHeader.SizeOfImage != kSizeOfImage)
    {
        detail = std::format("runtime is not nvngx_dlssg.dll 310.7.0 (timestamp 0x{:X}, image 0x{:X})", nt->FileHeader.TimeDateStamp, nt->OptionalHeader.SizeOfImage);
        return false;
    }

    auto archGates = FindArchGates(base, nt);
    size_t countMatches = 0;
    uint8_t* countGate = FindCountGate(base, nt, countMatches);
    const uint8_t* fatbin = nullptr;
    std::vector<uint64_t*> descriptors;
    uintptr_t faultAt = 0, faultIp = 0;
    if (!SafeFindDescriptors(base, nt, fatbin, descriptors, faultAt, faultIp))
    {
        HMODULE self = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)faultIp, &self);
        detail = std::format("kernel scan faulted reading 0x{:X} (runtime rva 0x{:X}) at ip rva 0x{:X}", faultAt, faultAt - (uintptr_t)base, faultIp - (uintptr_t)self);
        return false;
    }
    std::vector<uint8_t> kernel;
    std::string why;
    if (archGates.size() < kMinArchGates || archGates.size() > kMaxArchGates) { detail = std::format("{} arch gates, expected {} to {}", archGates.size(), kMinArchGates, kMaxArchGates); return false; }
    if (!countGate || memcmp(countGate, kCountGateOriginal, 2) != 0) { detail = std::format("count validator not found ({} matches)", countMatches); return false; }
    if (descriptors.empty() || !fatbin) { detail = "kernel descriptor not found"; return false; }
    if (!RebuildKernel(fatbin, kernel, why)) { detail = "kernel: " + why; return false; }

    // Everything is verified; now write. Any failed write rolls all of it back.
    bool ok = true;
    const uint8_t ada = 0x90;
    for (auto* site : archGates) ok = ok && Patch(site, &ada, 1);
    ok = ok && Patch(countGate, kCountGateReplacement, 2);
    g_kernel = VirtualAlloc(nullptr, kernel.size(), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    ok = ok && g_kernel != nullptr;
    if (ok)
    {
        memcpy(g_kernel, kernel.data(), kernel.size());
        uint64_t pointer = (uint64_t)g_kernel;
        for (auto* slot : descriptors) ok = ok && Patch(slot, &pointer, 8);
    }
    if (!ok)
    {
        RollBack();
        detail = std::format("a write failed ({}); runtime restored", GetLastError());
        return false;
    }
    detail = std::format("{} arch gates, count validator, {} kernel descriptors", archGates.size(), descriptors.size());
    return true;
}

void Release()
{
    std::lock_guard lock(g_mutex);
    if (g_saved.empty()) return;
    RollBack();
    Log(1, "x3/x4 unlock removed, runtime restored");
}

}
