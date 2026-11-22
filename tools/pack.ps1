<#
Builds an installer and an update package with Velopack.

  tools/pack.ps1 -Version 0.1.0
  tools/pack.ps1 -Version 0.1.1 -Output releases
  tools/pack.ps1 -Version 0.1.1 -NoNvidia

The package includes NVIDIA's Video Effects runtime for frame generation and RTX Video, SDK
(about 275 MB, fetched from NVIDIA's public package and checked against its hashes), so the app
works without a first download. -NoNvidia leaves it out; the app then downloads it on first start.

It also includes DLSS Frame Generation: nvngx_dlssg.dll 310.7.0 from NVIDIA's DLSS SDK (tools/dlss/fetch.ps1, about 8 MB)
and TretioNgx.dll, built from Native/TretioNgx with the MSVC x64 tools, with TretioNr.nvngx.dll for DLSS 5.
DLSS 5's runtime (192 MB) is not included; the app downloads it when DLSS 5 is picked.

It also includes the RIFE models (about 140 MB), built with tools/rife/export.py, which needs
Python with torch and onnx. Models already in ThirdParty/models are reused (delete them to rebuild). -NoRife leaves them out.

The output folder holds Setup.exe, the full package and the release files. The Release workflow
runs this script and publishes the folder as a GitHub release, where installed apps find updates.
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Output = "releases",
    [switch]$NoNvidia,
    [switch]$NoRife
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$publish = Join-Path $root "publish\$Version"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

& (Join-Path $PSScriptRoot "dlss\fetch.ps1")

dotnet publish TretioDLSS.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
foreach ($dll in "TretioNgx.dll", "TretioNr.nvngx.dll", "nvngx_dlssg.dll") {
    if (-not (Test-Path (Join-Path $publish $dll))) { throw "$dll is missing from the package" }
}

if (-not $NoNvidia) {
    & (Join-Path $publish "TretioDLSS.exe") --install all --to (Join-Path $publish "nvvfx") | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "fetching NVIDIA's Video Effects runtime failed" }
}

if (-not $NoRife) {
    if (-not (Test-Path ThirdParty/models/rife*.onnx)) {
        python tools/rife/export.py --out ThirdParty/models | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "building the RIFE models failed" }
    }
    New-Item -ItemType Directory -Force (Join-Path $publish "models") | Out-Null
    Copy-Item ThirdParty/models/rife*.onnx (Join-Path $publish "models")
}

dotnet tool restore | Out-Null
dotnet vpk pack --packId TretioDLSS --packVersion $Version --packDir $publish --mainExe TretioDLSS.exe --packTitle TretioDLSS --icon Resources\icon.ico --noPortable --outputDir $Output
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Get-ChildItem $Output | Select-Object Name, Length
