<#
Fetches what DLSS Frame Generation needs into ThirdParty/dlss from NVIDIA/DLSS on GitHub:
the NGX headers, the static NGX loader and nvngx_dlssg.dll. Pinned to 310.7.0: the x3 and x4
unlock for RTX 40 (Native/TretioNgx/MfgUnlock.cpp) is verified against this exact runtime.

  tools/dlss/fetch.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dest = Join-Path $root "ThirdParty\dlss"
$dlss = "https://raw.githubusercontent.com/NVIDIA/DLSS/v310.7.0"
$runtimeSha256 = "135EAF0733C1E37381A8C28ABCF7A862404A54132B81787C04E35D09EFC5E36F"

$files = @{}
foreach ($h in "nvsdk_ngx.h", "nvsdk_ngx_defs.h", "nvsdk_ngx_params.h", "nvsdk_ngx_helpers.h",
    "nvsdk_ngx_defs_dlssg.h", "nvsdk_ngx_params_dlssg.h", "nvsdk_ngx_helpers_dlssg.h", "nvsdk_ngx_helpers_dlssg_vk.h",
    "nvsdk_ngx_vk.h", "nvsdk_ngx_defs_vk.h", "nvsdk_ngx_helpers_vk.h") {
    $files["include\$h"] = "$dlss/include/$h"
}
$files["lib\nvsdk_ngx_s.lib"] = "$dlss/lib/Windows_x86_64/x64/nvsdk_ngx_s.lib"
$files["bin\nvngx_dlssg.dll"] = "$dlss/lib/Windows_x86_64/rel/nvngx_dlssg.dll"

$runtime = Join-Path $dest "bin\nvngx_dlssg.dll"
if ((Test-Path $runtime) -and (Get-FileHash $runtime -Algorithm SHA256).Hash -ne $runtimeSha256) {
    Write-Host "ThirdParty/dlss holds another SDK version; fetching 310.7.0"
    Remove-Item -Recurse -Force $dest
}

foreach ($entry in $files.GetEnumerator()) {
    $path = Join-Path $dest $entry.Key
    if (Test-Path $path) { continue }
    New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
    Write-Host "Fetching $($entry.Key)"
    Invoke-WebRequest -Uri $entry.Value -OutFile "$path.part" -UseBasicParsing
    Move-Item -Force "$path.part" $path
}
if ((Get-FileHash $runtime -Algorithm SHA256).Hash -ne $runtimeSha256) { throw "nvngx_dlssg.dll is not the expected 310.7.0 build" }
Write-Host "DLSS Frame Generation files are in $dest"
