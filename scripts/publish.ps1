<#
.SYNOPSIS
  Builds the OverInk release artifacts: a self-contained win-x64 publish, a portable zip,
  an Inno Setup installer (if Inno Setup is available) and SHA256 checksums.

.EXAMPLE
  ./scripts/publish.ps1 -Version 0.2.0
  ./scripts/publish.ps1 -Version 0.2.0 -SkipInstaller
#>
param(
    [string]$Version = "0.1.0",
    [string]$OutputDir = "artifacts",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root

$Version = $Version.TrimStart("v")
$artifacts = Join-Path $root $OutputDir
$publishDir = Join-Path $artifacts "publish"

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir | Out-Null

Write-Host "==> Publishing OverInk $Version (self-contained, win-x64)"
dotnet publish src/OverInk/OverInk.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishReadyToRun=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$zip = Join-Path $artifacts "OverInk-$Version-win-x64.zip"
Write-Host "==> Creating portable zip"
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zip -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    $iscc = if ($cmd) { $cmd.Source } else { $null }
    if (-not $iscc) {
        $candidates = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        )
        $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }

    if ($iscc) {
        Write-Host "==> Building installer with $iscc"
        & $iscc "/DAppVersion=$Version" "/DSourceDir=$publishDir" (Join-Path $root "installer/OverInk.iss")
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
    }
    else {
        Write-Warning "Inno Setup (ISCC.exe) not found - skipping installer. Install it with: winget install JRSoftware.InnoSetup"
    }
}

Write-Host "==> Writing checksums"
$sums = Get-ChildItem $artifacts -File |
    Where-Object { $_.Extension -in ".zip", ".exe" } |
    ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name }
$sums | Set-Content (Join-Path $artifacts "SHA256SUMS.txt") -Encoding ascii

Write-Host ""
Write-Host "Artifacts in $artifacts :"
Get-ChildItem $artifacts -File | ForEach-Object { "  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB) }
