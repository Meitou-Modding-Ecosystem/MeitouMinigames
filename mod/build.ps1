# PZA's Cat Crawlers for Meitou: build the code mod and stage its install folder. Author: PZA. Licence: GPLv3 or later.
#
#   .\build.ps1            build Release, stage out\PZACatCrawlers\ (PZACatCrawlers.Meitou.dll, meitou.json, PZACatCrawlers.mod, art\, lang\)
#   .\build.ps1 -Check     build and run the fuzz harness (used by release checks; never starts the game)
#   .\build.ps1 -Fuzz      also run the fuzz harness (tests\fuzz, PZA's Meitou UI fuzz kit); it never starts the game
#   -MeitouDir <dir>       the unpacked Meitou release to build against (default: MeitouDir in mod\local.props, git-ignored)
#
# The art (mod\art\*.png) is drawn by tools\art\make_cards.py and committed; this script only copies it.
# It installs nothing. out\ is git-ignored.
param(
    [switch]$Check,
    [switch]$Fuzz,
    [string]$MeitouDir = ""
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$localProps = Join-Path $here "local.props"
if ($MeitouDir -eq "" -and (Test-Path $localProps)) { $MeitouDir = ([xml](Get-Content $localProps -Raw)).Project.PropertyGroup.MeitouDir }
if (-not $MeitouDir) { throw "Set -MeitouDir <unpacked Meitou release> or create mod\local.props (see the comment in the csproj)" }
$MeitouDir = [System.IO.Path]::GetFullPath($MeitouDir)
if (-not (Test-Path (Join-Path $MeitouDir "MeitouClient.dll"))) { throw "MeitouClient.dll not found in $MeitouDir" }

& dotnet build (Join-Path $here "PZACatCrawlers.Meitou.csproj") -c Release -nologo "-p:MeitouDir=$MeitouDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }

$bin = Get-ChildItem (Join-Path $here "bin\Release") -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$stage = Join-Path $here "out\PZACatCrawlers"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force (Join-Path $stage "art") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $stage "lang") | Out-Null
Copy-Item (Join-Path $bin.FullName "PZACatCrawlers.Meitou.dll") $stage -Force
Copy-Item (Join-Path $here "meitou.json") $stage -Force
Copy-Item (Join-Path $here "PZACatCrawlers.mod") $stage -Force
Copy-Item (Join-Path $here "art\*.png") (Join-Path $stage "art") -Force
if (Test-Path (Join-Path $here "lang\*.txt")) { Copy-Item (Join-Path $here "lang\*.txt") (Join-Path $stage "lang") -Force }

Write-Host "staged $stage :"
Get-ChildItem $stage | ForEach-Object { Write-Host ("  {0,-30} {1,8}" -f $_.Name, $(if ($_.PSIsContainer) { "{0} files" -f (Get-ChildItem $_.FullName).Count } else { "$($_.Length) bytes" })) }

if ($Fuzz -or $Check) {
    $fuzzRunner = Join-Path $here "..\..\PZAMeitouUi\fuzz\run_fuzz.ps1"
    if (Test-Path $fuzzRunner) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $fuzzRunner -Repo (Join-Path $here "..") -MeitouDir $MeitouDir
        if ($LASTEXITCODE -ne 0) { throw "fuzz FAILED ($LASTEXITCODE): report in tests\out\fuzz\fuzz-report.txt" }
    } elseif ($Fuzz) {
        throw "fuzz runner not found at $fuzzRunner"
    }
}
