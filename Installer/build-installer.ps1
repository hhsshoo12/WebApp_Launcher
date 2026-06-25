param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$ProductVersion = "0.1.0"
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$dotnet = "C:\Users\hhsshoo12\scoop\apps\dotnet-sdk\current\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = "dotnet"
}

$python = "python"
$artifacts = Join-Path $repo "artifacts"
$publishRoot = Join-Path $artifacts "publish"
$payload = Join-Path $artifacts "installer-payload"
$pyinstallerWork = Join-Path $artifacts "pyinstaller"
$portableZip = Join-Path $artifacts "WebAppLauncher-$ProductVersion-portable-win-x64.zip"
$installerExe = Join-Path $artifacts "WebAppLauncher-Setup-$ProductVersion-win-x64.exe"
$venv = Join-Path $PSScriptRoot ".venv"

Remove-Item -LiteralPath $publishRoot,$payload,$pyinstallerWork -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $portableZip,$installerExe -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publishRoot,$payload,$pyinstallerWork | Out-Null

& $dotnet publish (Join-Path $repo "WebAppLauncher\WebAppLauncher.csproj") -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o (Join-Path $publishRoot "WebAppLauncher")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet publish (Join-Path $repo "WebAppLauncher.Cli\WebAppLauncher.Cli.csproj") -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o (Join-Path $publishRoot "WebAppLauncher.Cli")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet publish (Join-Path $repo "WebAppLauncher.Bootstrapper\WebAppLauncher.Bootstrapper.csproj") -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o (Join-Path $publishRoot "WebAppLauncher.Bootstrapper")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item -Path (Join-Path $publishRoot "WebAppLauncher\*") -Destination $payload -Recurse -Force
Copy-Item -LiteralPath (Join-Path $publishRoot "WebAppLauncher.Cli\WebAppLauncher.Cli.exe") -Destination $payload -Force
Copy-Item -LiteralPath (Join-Path $publishRoot "WebAppLauncher.Bootstrapper\WebAppLauncher.Bootstrapper.exe") -Destination $payload -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "runtime-catalog.toml") -Destination $payload -Force

Compress-Archive -Path (Join-Path $payload "*") -DestinationPath $portableZip -CompressionLevel Optimal

if (-not (Test-Path -LiteralPath $venv)) {
    & $python -m venv $venv
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$venvPython = Join-Path $venv "Scripts\python.exe"
& $venvPython -m pip install --disable-pip-version-check -r (Join-Path $PSScriptRoot "requirements-build.txt")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $venvPython (Join-Path $PSScriptRoot "prepare-logo.py")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$assets = Join-Path $PSScriptRoot "assets"
& $venvPython -m PyInstaller `
    --noconfirm `
    --clean `
    --onefile `
    --windowed `
    --name "WebAppLauncher-Setup-$ProductVersion-win-x64" `
    --distpath $artifacts `
    --workpath (Join-Path $pyinstallerWork "work") `
    --specpath (Join-Path $pyinstallerWork "spec") `
    --icon (Join-Path $assets "installer.ico") `
    --add-data "$(Join-Path $assets "logo.png");assets" `
    --add-data "$(Join-Path $assets "installer.ico");assets" `
    --add-data "$payload;payload" `
    (Join-Path $PSScriptRoot "installer.py")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$builtInstaller = Join-Path $artifacts "WebAppLauncher-Setup-$ProductVersion-win-x64.exe"
if (-not (Test-Path -LiteralPath $builtInstaller)) {
    throw "PyInstaller output was not found: $builtInstaller"
}

Write-Host "Built portable package: $portableZip"
Write-Host "Built GUI installer:    $installerExe"
