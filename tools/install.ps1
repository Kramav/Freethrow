<#
.SYNOPSIS
    Prepares a machine to build and run Freethrow.

.DESCRIPTION
    Verifies the .NET SDK, downloads the ONNX models Freethrow needs, checks them
    against known hashes, and builds the solution.

    The models are not committed to the repository: they are several megabytes of
    binary that version independently of the code. Hashes are verified because these
    files are fetched over the network and are then handed straight to an inference
    runtime.

.PARAMETER ModelDirectory
    Where to place the models. Defaults to the repository's models folder.

.PARAMETER SkipBuild
    Download and verify the models without building.

.EXAMPLE
    .\tools\install.ps1
#>
[CmdletBinding()]
param(
    [string] $ModelDirectory,
    [switch] $SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $ModelDirectory) {
    $ModelDirectory = Join-Path $repositoryRoot 'models'
}

# Converted from MediaPipe's originals by the OpenCV Zoo project, Apache-2.0.
$models = @(
    @{
        Name   = 'palm_detection.onnx'
        Uri    = 'https://huggingface.co/opencv/palm_detection_mediapipe/resolve/main/palm_detection_mediapipe_2023feb.onnx'
        Sha256 = '78FF51C38496B7FC8B8EBDB6CC8C1ABB02FA6C38427C6848254CDABA57FCCE7C'
    },
    @{
        Name   = 'hand_landmark.onnx'
        Uri    = 'https://huggingface.co/opencv/handpose_estimation_mediapipe/resolve/main/handpose_estimation_mediapipe_2023feb.onnx'
        Sha256 = 'DB0898AE717B76B075D9BF563AF315B29562E11F8DF5027A1EF07B02BEF6D81C'
    }
)

function Test-DotnetSdk {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw "The .NET SDK is not installed. Install it with 'winget install Microsoft.DotNet.SDK.8' and run this script again."
    }

    $version = (& dotnet --version).Trim()
    $major = [int]($version -split '\.')[0]
    if ($major -lt 8) {
        throw "Freethrow needs .NET SDK 8 or later; found $version."
    }

    Write-Host "  .NET SDK $version" -ForegroundColor DarkGray
}

function Install-Model {
    param([hashtable] $Model, [string] $Directory)

    $path = Join-Path $Directory $Model.Name

    if (Test-Path $path) {
        $existing = (Get-FileHash $path -Algorithm SHA256).Hash
        if ($existing -eq $Model.Sha256) {
            Write-Host "  $($Model.Name) already present" -ForegroundColor DarkGray
            return
        }

        Write-Host "  $($Model.Name) hash mismatch, downloading again" -ForegroundColor Yellow
    }

    Write-Host "  downloading $($Model.Name)..." -ForegroundColor DarkGray
    Invoke-WebRequest -Uri $Model.Uri -OutFile $path -UseBasicParsing -TimeoutSec 300

    $actual = (Get-FileHash $path -Algorithm SHA256).Hash
    if ($actual -ne $Model.Sha256) {
        Remove-Item $path -Force
        throw "$($Model.Name) failed verification. Expected $($Model.Sha256), got $actual. The file has been deleted."
    }

    $size = [math]::Round((Get-Item $path).Length / 1MB, 1)
    Write-Host "  $($Model.Name) verified ($size MB)" -ForegroundColor DarkGray
}

Write-Host ''
Write-Host 'Freethrow setup' -ForegroundColor Cyan
Write-Host ''

Write-Host 'Checking toolchain' -ForegroundColor White
Test-DotnetSdk

Write-Host ''
Write-Host "Installing models to $ModelDirectory" -ForegroundColor White
New-Item -ItemType Directory -Force -Path $ModelDirectory | Out-Null
foreach ($model in $models) {
    Install-Model -Model $model -Directory $ModelDirectory
}

if (-not $SkipBuild) {
    Write-Host ''
    Write-Host 'Building' -ForegroundColor White
    & dotnet build (Join-Path $repositoryRoot 'Freethrow.sln') --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE."
    }
}

Write-Host ''
Write-Host 'Ready.' -ForegroundColor Green
Write-Host ''
Write-Host '  Check the camera:   dotnet run --project demos\Freethrow.Demo.Preview -- --probe' -ForegroundColor DarkGray
Write-Host '  Track a hand:       dotnet run --project demos\Freethrow.Demo.Preview -- --track' -ForegroundColor DarkGray
Write-Host '  Open the preview:   dotnet run --project demos\Freethrow.Demo.Preview' -ForegroundColor DarkGray
Write-Host ''

if ($ModelDirectory -ne (Join-Path $repositoryRoot 'models')) {
    Write-Host "Models are outside the repository; set FREETHROW_MODELS=$ModelDirectory so they can be found." -ForegroundColor Yellow
    Write-Host ''
}
