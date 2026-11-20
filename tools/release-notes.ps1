param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$Repo = $env:GITHUB_REPOSITORY,
    [string]$Model = "deepseek-v4-pro",
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"
if (-not $env:DEEPSEEK_API_KEY) { throw "DEEPSEEK_API_KEY is not set" }

$prev = git tag --merged "$Tag^" --sort=-creatordate | Select-Object -First 1
$range = if ($prev) { "$prev..$Tag" } else { $Tag }

$log = git log $range --no-merges --reverse --format="--- %s" --stat=120 | Out-String
$context = "Commits in this release (subject and changed files):`n$log"
if (-not $prev) {
    $readme = git show "${Tag}:README.md" | Out-String
    $context = "This is the first release. README:`n$readme`n`n$context"
}

$system = @"
You write release notes for TretioDLSS, a Windows app that captures a console (for example a PS5 through a capture card) and shows it on a PC monitor with upscaling and frame generation (NVIDIA Frame Generation, DLSS Frame Generation, RIFE and others).

Write a Markdown bullet list in exactly this style:
* **Area:** Added an option to exclude clips and highlights from the storage limit.
* **Area:** Changed recordings to keep going when the drive runs low.
* **Area:** Fixed kill and assist detection after the latest update.
* **Area:** Improved the error shown when installing fails.

Rules:
- Each bullet starts with a short area in bold followed by a colon (for example Frame Generation, Upscaling, Capture, Output Window, Interface, Updates, Installer).
- Each sentence starts with Added, Changed, Fixed or Improved, is one sentence, and ends with a period.
- Order: all Added first, then Changed, then Fixed, then Improved.
- Describe what the user notices, in plain words, with the benefit when it is clear (for example less input delay or smoother motion). Write multipliers as x2, x4, x8. No commit hashes, file names, class names or internal terms.
- Skip changes users never notice: CI, docs, tests, refactors, and build or packaging script fixes.
- Merge related commits into one bullet. Keep it short.
- For a first release, list only the main features, all as Added bullets.
- Never use em-dashes. No headings, no intro, no closing text. Output only the list.
"@

$body = @{
    model    = $Model
    messages = @(
        @{ role = "system"; content = $system },
        @{ role = "user"; content = $context }
    )
} | ConvertTo-Json -Depth 5

$res = Invoke-RestMethod -Uri "https://api.deepseek.com/chat/completions" -Method Post `
    -Headers @{ Authorization = "Bearer $env:DEEPSEEK_API_KEY" } -ContentType "application/json; charset=utf-8" `
    -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 300
$notes = $res.choices[0].message.content.Trim() -replace [char]0x2014, ","
if (-not $notes) { throw "Empty release notes" }

Write-Host $notes
if ($DryRun) { return }
$file = New-TemporaryFile
Set-Content $file $notes -Encoding utf8
gh release edit $Tag --repo $Repo --notes-file $file
if ($LASTEXITCODE -ne 0) { throw "gh release edit failed" }
