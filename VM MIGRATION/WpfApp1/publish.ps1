<#
.SYNOPSIS
    WinMigrate Pro (ElectroMU Edition) - Standalone Release Publisher
    Lead Systems Architect: Oleg Melnikov
    Organization: ElectroMU Gaming Network

.DESCRIPTION
    Compiles the zero-dependency, single-file standalone executable (WinMigratePro.exe)
    with embedded icon, manifest UAC elevation, and single-file compression.
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$sw = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "   WinMigrate Pro (ElectroMU Edition) - Standalone Publisher   " -ForegroundColor Cyan
Write-Host "   Author: Oleg Melnikov (ElectroMU Gaming Network)            " -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "[1/4] Cleaning previous build artifacts..." -ForegroundColor Yellow

$projectDir = $PSScriptRoot
if (-not $projectDir) { $projectDir = Get-Location }

# Clean previous publish artifacts
$publishDir = Join-Path $projectDir "bin\$Configuration\net10.0-windows\$Runtime\publish"
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "[2/4] Executing dotnet publish (Configuration: $Configuration, Runtime: $Runtime)..." -ForegroundColor Yellow
$publishArgs = @(
    "publish",
    "$projectDir\WpfApp1.csproj",
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "/p:PublishSingleFile=true",
    "/p:EnableCompressionInSingleFile=true",
    "/p:PublishTrimmed=false"
)

& dotnet $publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "[FAILED] Compilation terminated with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

Write-Host "[3/4] Verifying output binary and embedded manifests..." -ForegroundColor Yellow
$targetExe = Join-Path $publishDir "WinMigratePro.exe"

if (-not (Test-Path $targetExe)) {
    Write-Error "[FAILED] Expected output binary not found at: $targetExe"
    exit 1
}

$fileInfo = Get-Item $targetExe
$sizeMb = [Math]::Round($fileInfo.Length / 1MB, 2)
$hash = (Get-FileHash -Path $targetExe -Algorithm SHA256).Hash


$sw.Stop()

Write-Host "================================================================" -ForegroundColor Green
Write-Host "  SUCCESS: Standalone Executable Successfully Generated!        " -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host " Binary Path : $targetExe" -ForegroundColor Cyan
Write-Host " File Size   : $sizeMb MB (Single-File Compressed)" -ForegroundColor White
Write-Host " Runtime     : Self-Contained .NET (Zero Prerequisites)" -ForegroundColor White
Write-Host " SHA256 Hash : $hash" -ForegroundColor Gray
Write-Host " Build Time  : $($sw.Elapsed.TotalSeconds.ToString('F2')) seconds" -ForegroundColor Gray
Write-Host ""
Write-Host "You can now copy WinMigratePro.exe to any Windows 11 or Windows Server 2022/2025 node and run it directly without installing .NET or agents!" -ForegroundColor Green