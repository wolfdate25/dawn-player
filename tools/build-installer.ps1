<#
.SYNOPSIS
    Dawn Player 배포 및 인스톨러 빌드 자동화 스크립트

.DESCRIPTION
    1. DawnPlayer.App 프로젝트를 Self-Contained WinUI 3 Release 빌드로 dotnet publish 합니다.
    2. 무설치 포터블 ZIP 아카이브를 생성합니다.
    3. Inno Setup 6 (ISCC.exe)를 사용하여 단일 실행 파일 인스톨러(.exe)를 빌드합니다.
    4. 생성된 배포 아티팩트의 SHA256 체크섬을 계산합니다.

.PARAMETER Version
    배포 버전 (기본값: "1.0.0")

.PARAMETER Configuration
    빌드 구성 (기본값: "Release")

.PARAMETER Runtime
    타겟 런타임 식별자 (기본값: "win-x64")

.PARAMETER SkipPublish
    이미 생성된 publish 디렉토리를 재사용하고 dotnet publish를 건너뜁니다.

.PARAMETER SkipZip
    포터블 ZIP 생성을 건너뜁니다.

.PARAMETER SkipInstaller
    Inno Setup 인스톨러 컴파일을 건너뜁니다.

.EXAMPLE
    pwsh -File tools/build-installer.ps1 -Version "1.0.0"
#>

[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipPublish,
    [switch]$SkipZip,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

# 1. 경로 설정
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
$AppProjPath = Join-Path $RootDir "src\DawnPlayer.App\DawnPlayer.App.csproj"
$DistDir = Join-Path $RootDir "dist"
$PublishDir = Join-Path $DistDir "publish"
$InstallerOutDir = Join-Path $DistDir "installer"
$IssScriptPath = Join-Path $RootDir "installer\DawnPlayer.iss"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   Dawn Player Packaging & Installer Build Tool" -ForegroundColor Cyan
Write-Host "   Version:       $Version" -ForegroundColor Yellow
Write-Host "   Configuration: $Configuration" -ForegroundColor Yellow
Write-Host "   Runtime:       $Runtime" -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Cyan

# 2. Dotnet Publish 실행
if (-not $SkipPublish) {
    Write-Host "`n[1/4] Publishing DawnPlayer.App (Self-Contained)..." -ForegroundColor Green
    
    if (Test-Path $PublishDir) {
        Write-Host "  Cleaning existing publish directory: $PublishDir" -ForegroundColor Gray
        Remove-Item -Path $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

    $publishArgs = @(
        "publish",
        $AppProjPath,
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", "true",
        "-p:WindowsPackageType=None",
        "-p:WindowsAppSDKSelfContained=true",
        "-p:Version=$Version",
        "-o", $PublishDir
    )

    Write-Host "  Running: dotnet $($publishArgs -join ' ')" -ForegroundColor Gray
    & dotnet @publishArgs

    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    # Copy LICENSE and THIRD-PARTY-NOTICES.md to publish directory
    $licenseFile = Join-Path $RootDir "LICENSE"
    $noticesFile = Join-Path $RootDir "THIRD-PARTY-NOTICES.md"
    if (Test-Path $licenseFile) { Copy-Item -Path $licenseFile -Destination $PublishDir -Force }
    if (Test-Path $noticesFile) { Copy-Item -Path $noticesFile -Destination $PublishDir -Force }

    Write-Host "  Publish succeeded. Output: $PublishDir" -ForegroundColor Green
} else {
    Write-Host "`n[1/4] Skipping publish (reusing $PublishDir)..." -ForegroundColor Yellow
    if (-not (Test-Path $PublishDir)) {
        Write-Error "Publish directory not found at '$PublishDir'. Cannot skip publish."
        exit 1
    }
}

# 3. 포터블 ZIP 아카이브 생성
$PortableZipPath = Join-Path $DistDir "DawnPlayer-v$Version-portable-$Runtime.zip"
if (-not $SkipZip) {
    Write-Host "`n[2/4] Creating Portable ZIP package..." -ForegroundColor Green

    if (Test-Path $PortableZipPath) {
        Remove-Item -Path $PortableZipPath -Force
    }

    Write-Host "  Compressing $PublishDir -> $PortableZipPath" -ForegroundColor Gray
    # ZipFile instead of Compress-Archive: the publish tree is hundreds of MB / thousands of
    # files, which Compress-Archive handles slowly and caps at 2 GB per entry.
    Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $PublishDir, $PortableZipPath,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)

    # The portable marker goes into the ARCHIVE ONLY. Creating it inside $PublishDir made the
    # installer depend on DawnPlayer.iss's Excludes list to stay non-portable — edit that list
    # and installed builds would silently write their library next to Program Files.
    $zip = [System.IO.Compression.ZipFile]::Open($PortableZipPath, 'Update')
    try {
        $null = $zip.CreateEntry('portable.dat')
        $null = $zip.CreateEntry('data/')
    } finally {
        $zip.Dispose()
    }

    $zipSizeMb = [math]::Round((Get-Item $PortableZipPath).Length / 1MB, 2)
    Write-Host "  Portable ZIP created: $PortableZipPath ($zipSizeMb MB)" -ForegroundColor Green
} else {
    Write-Host "`n[2/4] Skipping Portable ZIP creation..." -ForegroundColor Yellow
}

# 4. Inno Setup 6 컴파일러 탐색 및 인스톨러 빌드
if (-not $SkipInstaller) {
    Write-Host "`n[3/4] Building Inno Setup 6 Installer..." -ForegroundColor Green

    # ISCC 탐색
    $isccCmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    $isccPath = $null

    if ($isccCmd) {
        $isccPath = $isccCmd.Source
    } else {
        $candidatePaths = @(
            (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe",
            (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
        )

        foreach ($cand in $candidatePaths) {
            if (Test-Path $cand) {
                $isccPath = $cand
                break
            }
        }
    }

    if (-not $isccPath) {
        Write-Warning "Inno Setup 6 (ISCC.exe) was not found on this system."
        Write-Warning "To install Inno Setup 6, run: winget install --id JRSoftware.InnoSetup"
        Write-Error "Cannot build installer without ISCC.exe."
        exit 1
    }

    Write-Host "  Found Inno Setup Compiler: $isccPath" -ForegroundColor Gray

    if (-not (Test-Path $InstallerOutDir)) {
        New-Item -ItemType Directory -Path $InstallerOutDir -Force | Out-Null
    }

    $isccArgs = @(
        "/DMyAppVersion=$Version",
        "/DMySourceDir=$PublishDir",
        "/DMyOutputDir=$InstallerOutDir",
        $IssScriptPath
    )

    Write-Host "  Compiling: `"$isccPath`" $($isccArgs -join ' ')" -ForegroundColor Gray
    & $isccPath @isccArgs

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Inno Setup compilation failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    $InstallerExePath = Join-Path $InstallerOutDir "DawnPlayer-Setup-v$Version-x64.exe"
    if (Test-Path $InstallerExePath) {
        $exeSizeMb = [math]::Round((Get-Item $InstallerExePath).Length / 1MB, 2)
        Write-Host "  Installer EXE created: $InstallerExePath ($exeSizeMb MB)" -ForegroundColor Green
    }
} else {
    Write-Host "`n[3/4] Skipping Inno Setup installer compilation..." -ForegroundColor Yellow
}

# 5. 체크섬 생성 및 요약 보고서
Write-Host "`n[4/4] Generating Checksums and Summary..." -ForegroundColor Green
$Artifacts = @()

if (Test-Path $PortableZipPath) {
    $Artifacts += (Get-Item $PortableZipPath)
}

$InstallerExePath = Join-Path $InstallerOutDir "DawnPlayer-Setup-v$Version-x64.exe"
if (Test-Path $InstallerExePath) {
    $Artifacts += (Get-Item $InstallerExePath)
}

$ChecksumLines = @()

Write-Host "`n==========================================================" -ForegroundColor Cyan
Write-Host "                   Build Artifacts" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

foreach ($art in $Artifacts) {
    $hash = (Get-FileHash -Path $art.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $sizeMb = [math]::Round($art.Length / 1MB, 2)
    $ChecksumLines += "$hash *$($art.Name)"
    Write-Host "File:   $($art.Name) ($sizeMb MB)" -ForegroundColor White
    Write-Host "Path:   $($art.FullName)" -ForegroundColor Gray
    Write-Host "SHA256: $hash" -ForegroundColor Yellow
    Write-Host "----------------------------------------------------------" -ForegroundColor DarkGray
}

if ($Artifacts.Count -eq 0) {
    Write-Warning "No artifacts were produced (both -SkipZip and -SkipInstaller?). Skipping SHA256SUMS.txt."
} else {
    $ChecksumFilePath = Join-Path $DistDir "SHA256SUMS.txt"
    # WriteAllLines, not Out-File -Encoding utf8: the latter emits a BOM under Windows
    # PowerShell 5.1, which makes `sha256sum -c` reject the first line.
    [System.IO.File]::WriteAllLines($ChecksumFilePath, $ChecksumLines)
    Write-Host "Checksum file written: $ChecksumFilePath" -ForegroundColor Gray
}

Write-Host "`nPackaging completed successfully!" -ForegroundColor Green
