<#
Builds TretioStream.dll, the native part of the built-in GameStream host:
ENet (control channel), nanors (FEC), NVENC and DNS-SD. Needs the MSVC x64 tools; all sources are in this folder.

  Native/TretioStream/build.ps1 [-Out <dir>]
#>
param([string]$Out)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $here)
if (-not $Out) { $Out = Join-Path $root "Native\build" }

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "MSVC x64 tools not found" }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"

New-Item -ItemType Directory -Force $Out | Out-Null
$obj = Join-Path $Out "obj-stream"
New-Item -ItemType Directory -Force $obj | Out-Null
$v = Join-Path $here "vendor"
$c = @(Get-ChildItem "$v\enet" -Filter *.c) + @(Get-ChildItem "$v\nanors" -Filter *.c) | ForEach-Object { "`"$($_.FullName)`"" }
$dll = Join-Path $Out "TretioStream.dll"
$inc = "/I`"$v\enet\include`" /I`"$v\nanors`" /I`"$v\nvenc`""
$env:PATH = "$env:PATH;$(Split-Path -Parent $vswhere)"
$cmd = "`"$vcvars`" >nul && cl /nologo /O2 /MT /W1 /DNDEBUG /DHAS_SOCKLEN_T /D_WINSOCK_DEPRECATED_NO_WARNINGS $inc /Fo`"$obj/`" /c $($c -join ' ') && cl /nologo /std:c++20 /O2 /MT /EHsc /W3 /DNDEBUG $inc /Fo`"$obj/`" /c `"$here\TretioStream.cpp`" && link /nologo /DLL /OUT:`"$dll`" /IMPLIB:`"$obj\TretioStream.lib`" `"$obj\*.obj`" ws2_32.lib winmm.lib dnsapi.lib d3d11.lib"
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw "TretioStream build failed" }
Write-Host "Built $dll"
