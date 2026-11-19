<#
Records TretioDLSS with PresentMon and prints present mode, tearing, sync interval and
MsBetweenDisplayChange percentiles.

  tools/presentmon.ps1 -Seconds 10
  tools/presentmon.ps1 -Launch '--headless --source pattern:60in60 --frames 1200 --output monitor:0' -TargetMs 16.667
#>
param(
    [int]$Seconds = 10,
    [string]$Launch = "",
    [double]$TargetMs = 0,
    [string]$Output = "",
    [string]$ProcessName = "TretioDLSS.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "2.6.0"
$pmDir = Join-Path $root "ThirdParty\presentmon"
$pmExe = Join-Path $pmDir "PresentMon-$version-x64.exe"

# PresentMon needs ETW access: an elevated shell or the "Performance Log Users" group.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$inGroup = $identity.Groups | Where-Object { $_.Value -eq "S-1-5-32-559" }
if (-not $elevated -and -not $inGroup) {
    Write-Host "PresentMon cannot record: this shell is not elevated and the account is not in 'Performance Log Users'."
    Write-Host "Fix one of these, then run the script again:"
    Write-Host "  1. Run the terminal as administrator, or"
    Write-Host "  2. Add the account to the group (compmgmt.msc, Local Users and Groups, Groups, Performance Log Users), then sign out and in."
    Write-Host "     From an elevated shell: net localgroup ""Performance Log Users"" $env:USERNAME /add"
    exit 2
}

if (-not (Test-Path $pmExe)) {
    New-Item -ItemType Directory -Force $pmDir | Out-Null
    $url = "https://github.com/GameTechDev/PresentMon/releases/download/v$version/PresentMon-$version-x64.exe"
    Write-Host "Downloading PresentMon $version"
    Invoke-WebRequest -Uri $url -OutFile $pmExe -UseBasicParsing
}

if ($Output -eq "") { $Output = Join-Path $env:TEMP ("presentmon-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".csv") }

$app = $null
if ($Launch -ne "") {
    $exe = Get-ChildItem -Recurse -Filter TretioDLSS.exe (Join-Path $root "bin") | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { throw "TretioDLSS.exe not found under bin. Build first." }
    $app = Start-Process -FilePath $exe.FullName -ArgumentList $Launch -PassThru
    Start-Sleep -Seconds 3
}

& $pmExe --process_name $ProcessName --output_file $Output --timed $Seconds --terminate_after_timed --stop_existing_session --no_console_stats --v1_metrics | Out-Null
if ($app -and -not $app.HasExited) { $app.WaitForExit(30000) | Out-Null }

if (-not (Test-Path $Output)) { Write-Host "PresentMon wrote no file. Was $ProcessName presenting?"; exit 1 }
$rows = Import-Csv $Output
if ($rows.Count -eq 0) { Write-Host "No presents recorded for $ProcessName."; exit 1 }

function Get-Pct($values, $p) {
    $sorted = $values | Sort-Object
    $i = [Math]::Min($sorted.Count - 1, [Math]::Max(0, [int][Math]::Round($p * ($sorted.Count - 1))))
    return $sorted[$i]
}

$display = $rows | Where-Object { $_.MsBetweenDisplayChange -and [double]$_.MsBetweenDisplayChange -gt 0 } | ForEach-Object { [double]$_.MsBetweenDisplayChange }
Write-Host "File: $Output"
Write-Host ("Presents: {0}" -f $rows.Count)
$rows | Group-Object PresentMode | ForEach-Object { Write-Host ("Present mode: {0} ({1})" -f $_.Name, $_.Count) }
$rows | Group-Object AllowsTearing | ForEach-Object { Write-Host ("Allows tearing: {0} ({1})" -f $_.Name, $_.Count) }
$rows | Group-Object SyncInterval | ForEach-Object { Write-Host ("Sync interval: {0} ({1})" -f $_.Name, $_.Count) }
if ($display.Count -gt 0) {
    $p01 = Get-Pct $display 0.01; $p50 = Get-Pct $display 0.5; $p99 = Get-Pct $display 0.99
    Write-Host ("MsBetweenDisplayChange p01 {0:0.000}  p50 {1:0.000}  p99 {2:0.000}" -f $p01, $p50, $p99)
    if ($TargetMs -gt 0) {
        $dev = [Math]::Max([Math]::Abs($p99 - $TargetMs), [Math]::Abs($p01 - $TargetMs))
        Write-Host ("Largest p01/p99 deviation from {0:0.000} ms: {1:0.000} ms" -f $TargetMs, $dev)
    }
}
