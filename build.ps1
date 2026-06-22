[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$OneDir
)

$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$VenvPath = "A:\Dev\.venv"
$RustProject = Join-Path $ProjectRoot "rust\wapk-webview"
$RustTargetDir = Join-Path $env:TEMP "wapk-webview-target"
$RustExe = Join-Path $RustTargetDir "release\wapk-webview.exe"
$WebView2Loader = $null
$DistDir = Join-Path $ProjectRoot "dist"
$BuildDir = Join-Path $ProjectRoot "build"
$SpecFile = Join-Path $ProjectRoot "wapk-launcher.spec"
$EntryPoint = Join-Path $ProjectRoot "packaging\pyinstaller_entry.py"

function Require-Command {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $Name"
    }
}

Require-Command "uv"
Require-Command "cargo"

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

if ($Clean) {
    Remove-Item -LiteralPath $DistDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $BuildDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $SpecFile -Force -ErrorAction SilentlyContinue
}

$env:UV_PROJECT_ENVIRONMENT = $VenvPath
$env:CARGO_TARGET_DIR = $RustTargetDir

Write-Host "Building Rust WebView helper..."
Invoke-Native cargo build --manifest-path (Join-Path $RustProject "Cargo.toml") --release

if (-not (Test-Path -LiteralPath $RustExe)) {
    throw "Rust WebView helper was not built: $RustExe"
}

$WebView2Loader = Get-ChildItem `
    -Path (Join-Path $RustTargetDir "release\build") `
    -Recurse `
    -Filter "WebView2Loader.dll" |
    Where-Object { $_.FullName -match "\\x64\\WebView2Loader\.dll$" } |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $WebView2Loader) {
    throw "WebView2Loader.dll was not found in the Rust build output."
}

Write-Host "Checking Python sources..."
Invoke-Native uv run --project $ProjectRoot python -m compileall src tests

Write-Host "Running tests..."
Invoke-Native uv run --project $ProjectRoot python -m unittest discover -s tests

$PyInstallerMode = if ($OneDir) { "--onedir" } else { "--onefile" }
$WindowMode = "--windowed"
$AddRustExe = "$RustExe;."
$AddWebView2Loader = "$WebView2Loader;."

Write-Host "Building launcher executable with PyInstaller..."
Invoke-Native uv run --project $ProjectRoot --with pyinstaller pyinstaller `
    --noconfirm `
    --clean `
    $PyInstallerMode `
    $WindowMode `
    --name wapk-launcher `
    --paths (Join-Path $ProjectRoot "src") `
    --add-binary $AddRustExe `
    --add-binary $AddWebView2Loader `
    $EntryPoint

if ($OneDir) {
    $Output = Join-Path $DistDir "wapk-launcher\wapk-launcher.exe"
} else {
    $Output = Join-Path $DistDir "wapk-launcher.exe"
}

if (-not (Test-Path -LiteralPath $Output)) {
    throw "PyInstaller output was not created: $Output"
}

Write-Host "Build complete: $Output"
