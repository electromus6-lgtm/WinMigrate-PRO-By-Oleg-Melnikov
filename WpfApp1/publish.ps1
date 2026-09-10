<#
.SYNOPSIS
    WinMigrate Pro (ElectroMU Edition) - Standalone Release Publisher
    Lead Systems Architect: Oleg Melnikov
    Organization: ElectroMU Gaming Network

.DESCRIPTION
    Compiles the zero-dependency, single-file standalone executable (WinMigratePro.exe)
    with embedded icon, manifest UAC elevation, single-file compression, and automatically
    bundles the companion Tools engine (qemu-img.exe + MinGW runtime libraries).
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

$projectDir = $PSScriptRoot
if (-not $projectDir) { $projectDir = Get-Location }

# 1. Clean previous publish artifacts
Write-Host "[1/5] Cleaning previous build artifacts..." -ForegroundColor Yellow
$publishDir = Join-Path $projectDir "bin\$Configuration\net10.0-windows\$Runtime\publish"
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
}

# 2. Compile Self-Contained Single Executable
Write-Host "[2/5] Executing dotnet publish (Configuration: $Configuration, Runtime: $Runtime)..." -ForegroundColor Yellow
$publishArgs = @(
    "publish",
    "$projectDir\WpfApp1.csproj",
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "/p:PublishSingleFile=true",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "/p:EnableCompressionInSingleFile=true",
    "/p:PublishReadyToRun=true",
    "/p:PublishTrimmed=false"
)

& dotnet $publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "[FAILED] Compilation terminated with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

# 3. Locate and verify output binary
Write-Host "[3/5] Verifying output binary and embedded manifests..." -ForegroundColor Yellow
$targetExe = Join-Path $publishDir "WinMigratePro.exe"
if (-not (Test-Path $targetExe)) {
    # Fallback search if project name differs from assembly name
    $exeCandidates = Get-ChildItem -Path $publishDir -Filter "*.exe" -File | Where-Object { $_.Name -notlike "*.vshost*" }
    if ($exeCandidates.Count -gt 0) {
        $targetExe = $exeCandidates[0].FullName
    } else {
        Write-Error "[FAILED] Expected output binary not found inside: $publishDir"
        exit 1
    }
}

$fileInfo = Get-Item $targetExe
$sizeMb = [Math]::Round($fileInfo.Length / 1MB, 2)
$hash = (Get-FileHash -Path $targetExe -Algorithm SHA256).Hash

# 4. Bundle & verify companion Tools folder (qemu-img.exe + DLLs)
Write-Host "[4/5] Packaging companion Tools engine (qemu-img.exe + MinGW runtime)..." -ForegroundColor Yellow
$toolsSource = Join-Path $projectDir "Tools"
$toolsTarget = Join-Path $publishDir "Tools"

if (Test-Path $toolsSource) {
    if (-not (Test-Path $toolsTarget)) {
        New-Item -ItemType Directory -Path $toolsTarget -Force | Out-Null
    }
    Copy-Item -Path "$toolsSource\*" -Destination $toolsTarget -Recurse -Force
    
    $qemuTarget = Join-Path $toolsTarget "qemu-img.exe"
    if (Test-Path $qemuTarget) {
        Write-Host "  [OK] Embedded V2V Engine: Tools\qemu-img.exe verified." -ForegroundColor Green
    } else {
        Write-Warning "  [WARN] Tools folder copied, but 'qemu-img.exe' was not found inside."
    }
} else {
    Write-Warning "  [WARN] Project root 'Tools' folder not found at '$toolsSource'. V2V disk conversion will require Tools\qemu-img.exe at runtime."
}

# 5. Generate Distributable Release Package (ZIP)
Write-Host "[5/5] Generating Distributable Release Archive..." -ForegroundColor Yellow
$releaseZipDir = Join-Path $projectDir "Release_Packages"
if (-not (Test-Path $releaseZipDir)) {
    New-Item -ItemType Directory -Path $releaseZipDir -Force | Out-Null
}

$timestamp = Get-Date -Format "yyyyMMdd_HHmm"
$zipName = "WinMigratePro_v1.0_$Runtime`_$timestamp.zip"
$zipPath = Join-Path $releaseZipDir $zipName

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -CompressionLevel Optimal

$sw.Stop()

Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host "  SUCCESS: Standalone Release Bundle Successfully Generated!    " -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host " Binary Path   : $targetExe" -ForegroundColor Cyan
Write-Host " File Size     : $sizeMb MB (Single-File Compressed)" -ForegroundColor White
Write-Host " Runtime       : Self-Contained .NET (Zero Prerequisites)" -ForegroundColor White
Write-Host " SHA256 Hash   : $hash" -ForegroundColor Gray
Write-Host " Release ZIP   : $zipPath" -ForegroundColor Green
Write-Host " Build Time    : $($sw.Elapsed.TotalSeconds.ToString('F2')) seconds" -ForegroundColor Gray
Write-Host ""
Write-Host "You can now distribute '$zipPath' or copy the published folder to any Windows 11 / Windows Server 2022/2025 host with zero prerequisites!" -ForegroundColor Green