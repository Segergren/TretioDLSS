<#
Builds TretioNgx.dll, the native bridge to NVIDIA's NGX (DLSS Frame Generation and DLSS 5 Neural Rendering),
and TretioNr.nvngx.dll, the module that makes the calls into the DLSS 5 runtime.
Needs the MSVC x64 tools and ThirdParty/dlss (tools/dlss/fetch.ps1).

  Native/TretioNgx/build.ps1 [-Out <dir>]
#>
param([string]$Out)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $here)
$sdk = Join-Path $root "ThirdParty\dlss"
if (-not $Out) { $Out = Join-Path $root "Native\build" }
if (-not (Test-Path (Join-Path $sdk "lib\nvsdk_ngx_s.lib"))) { throw "ThirdParty/dlss is missing; run tools/dlss/fetch.ps1" }

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "MSVC x64 tools not found" }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"

New-Item -ItemType Directory -Force $Out | Out-Null
$obj = Join-Path $Out "obj"
New-Item -ItemType Directory -Force $obj | Out-Null
$sources = (Get-ChildItem $here -Filter *.cpp | ForEach-Object { "`"$($_.FullName)`"" }) -join " "
$dll = Join-Path $Out "TretioNgx.dll"
$env:PATH = "$env:PATH;$(Split-Path -Parent $vswhere)"
$cmd = "`"$vcvars`" >nul && cl /nologo /std:c++20 /O2 /MT /EHsc /W3 /DNDEBUG /I`"$sdk\include`" /Fo`"$obj/`" $sources /LD /link /OUT:`"$dll`" /IMPLIB:`"$obj\TretioNgx.lib`" /LIBPATH:`"$sdk\lib`" nvsdk_ngx_s.lib d3d12.lib dxgi.lib advapi32.lib user32.lib bcrypt.lib version.lib"
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw "TretioNgx build failed" }
Write-Host "Built $dll"

$callerObj = Join-Path $obj "caller"
New-Item -ItemType Directory -Force $callerObj | Out-Null
$caller = Join-Path $Out "TretioNr.nvngx.dll"
$cmd = "`"$vcvars`" >nul && cl /nologo /O2 /MT /W3 /DNDEBUG /Fo`"$callerObj/`" `"$here\caller\NrCaller.cpp`" /LD /link /OUT:`"$caller`" /IMPLIB:`"$callerObj\TretioNr.lib`""
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw "TretioNr.nvngx.dll build failed" }
Write-Host "Built $caller"
