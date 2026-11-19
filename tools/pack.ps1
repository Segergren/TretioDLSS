<#
Builds an installer and an update package with Velopack.

  tools/pack.ps1 -Version 0.1.0
  tools/pack.ps1 -Version 0.1.1 -Output releases
  tools/pack.ps1 -Version 0.1.1 -NoNvidia

The package includes NVIDIA's Video Effects runtime for frame generation and RTX Video, SDK
(about 275 MB, fetched from NVIDIA's public package and checked against its hashes), so the app
works without a first download. -NoNvidia leaves it out; the app then downloads it on first start.

It also includes the RIFE models (about 140 MB), built with tools/rife/export.py, which needs
Python with torch and onnx. Models already in ThirdParty/models are reused. -NoRife leaves them out.

The output folder holds Setup.exe, the full package and RELEASES. Pointing the app's update
source (Settings, Advanced) at that folder lets an installed app update from it.
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

dotnet publish TretioDLSS.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

if (-not $NoNvidia) {
    & (Join-Path $publish "TretioDLSS.exe") --install all --to (Join-Path $publish "nvvfx") | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "fetching NVIDIA's Video Effects runtime failed" }
}

if (-not $NoRife) {
    python tools/rife/export.py --out ThirdParty/models | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "building the RIFE models failed" }
    New-Item -ItemType Directory -Force (Join-Path $publish "models") | Out-Null
    Copy-Item ThirdParty/models/rife*.onnx (Join-Path $publish "models")
}

dotnet tool restore | Out-Null
dotnet vpk pack --packId TretioDLSS --packVersion $Version --packDir $publish --mainExe TretioDLSS.exe --packTitle TretioDLSS --icon Resources\icon.ico --outputDir $Output
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Get-ChildItem $Output | Select-Object Name, Length
