#pragma once

#include <windows.h>

#include <string>

void Log(int level, const std::string& message);

// x3 and x4 DLSS Frame Generation on RTX 40, patched into the loaded nvngx_dlssg.dll 310.7.0 in memory.
// Ported from Veyra's AdaMfgUnlock (itself from dashdogy/RTX40MFG-Unlock and ImDreamt/MFGAdaUnlock-RenoDx).
namespace MfgUnlock {

bool IsAda(UINT vendorId, UINT deviceId);
bool Apply(HMODULE runtime, std::string& detail);
void Release();

}
