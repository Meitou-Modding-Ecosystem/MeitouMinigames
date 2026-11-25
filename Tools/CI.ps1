param(
    [Parameter(Mandatory)][ValidateSet('0.21.0', '0.22.1')][string]$MeitouVersion,
    [Parameter(Mandatory)][string]$Scratch,
    [string]$MeitouDir = '',
    [switch]$Mutation
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$kit = Join-Path (Split-Path $repo -Parent) 'PZAMeitouUi'
$runner = Join-Path $kit 'fuzz/run_fuzz.ps1'
if (-not (Test-Path -LiteralPath $runner)) { throw "Required fuzz kit missing: $runner" }
$Scratch = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Scratch)
New-Item -ItemType Directory -Force -Path $Scratch | Out-Null
if (-not $MeitouDir) {
    $hashes = @{
        '0.21.0' = '7aff07500dc14931809ba9396603486c671a42700e9129a4fbda5e702e5a1953'
        '0.22.1' = 'e9d1c41330d0859902b35db450209f86a5cf5093c846545153c3eb131161375f'
    }
    $archive = Join-Path $Scratch "Meitou-$MeitouVersion-win-x64.zip"
    if (-not (Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest -Uri "https://github.com/VlcekM/MeitouClient/releases/download/v$MeitouVersion/Meitou-$MeitouVersion-win-x64.zip" -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $hashes[$MeitouVersion]) { throw 'Release archive SHA-256 mismatch' }
    $expanded = Join-Path $Scratch $MeitouVersion
    Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
    $MeitouDir = Join-Path $expanded "Meitou-$MeitouVersion-win-x64"
}
$MeitouDir = (Resolve-Path -LiteralPath $MeitouDir).Path
$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $MeitouDir 'MeitouClient.dll')).ProductVersion
if ($version.Split('+')[0] -ne $MeitouVersion) { throw "Expected Meitou $MeitouVersion, got $version" }
Write-Host "Building and fuzzing against Meitou $version at $MeitouDir"
# Both the mod and fuzz harness use this matrix-selected release; no default local reader path.
& (Join-Path $repo 'mod/build.ps1') -Fuzz -MeitouDir $MeitouDir
if ($LASTEXITCODE -ne 0) { throw "Build/fuzz failed ($LASTEXITCODE)" }
$stage = Join-Path $repo 'mod/out/PZACatCrawlers'
foreach ($file in 'PZACatCrawlers.Meitou.dll', 'PZACatCrawlers.mod', 'meitou.json') {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $file))) { throw "Staged $file missing" }
}
if (-not (Get-ChildItem -LiteralPath (Join-Path $stage 'art') -Filter '*.png')) { throw 'Staged card art missing' }
if ($Mutation) {
    # mutate.py resolves its reader path from local.props in each disposable copy.
    $props = "<Project><PropertyGroup><MeitouDir>$([System.Security.SecurityElement]::Escape($MeitouDir))</MeitouDir></PropertyGroup></Project>"
    $localProps = Join-Path $repo 'mod/local.props'
    $previousProps = if (Test-Path -LiteralPath $localProps) { [System.IO.File]::ReadAllBytes($localProps) } else { $null }
    try {
        Set-Content -LiteralPath $localProps -Value $props -Encoding utf8
        & python -I (Join-Path $kit 'fuzz/mutate.py') $repo
        if ($LASTEXITCODE -ne 0) { throw "Mutation check failed ($LASTEXITCODE)" }
    } finally {
        if ($null -ne $previousProps) { [System.IO.File]::WriteAllBytes($localProps, $previousProps) }
        else { Remove-Item -LiteralPath $localProps }
    }
}
