param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$ProductVersion = "0.1.2"
)

$ErrorActionPreference = "Continue"
Set-Location -LiteralPath $PSScriptRoot

$repo = Split-Path -Parent $PSScriptRoot
$dotnet = "C:\Users\hhsshoo12\scoop\apps\dotnet-sdk\current\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = "dotnet"
}

$python = "python"
$artifacts = Join-Path $repo "artifacts"
$publishRoot = Join-Path $artifacts "publish"
$launcherPayload = Join-Path $artifacts "launcher-payload"
$pyinstallerWork = Join-Path $artifacts "pyinstaller"
$launcherZip = Join-Path $artifacts "WAPL-Launcher-v$ProductVersion.zip"
$launcherSha = Join-Path $artifacts "WAPL-Launcher-v$ProductVersion.zip.sha256"
$installerExe = Join-Path $artifacts "WebAppLauncher-Setup-v$ProductVersion.exe"
$venv = Join-Path $PSScriptRoot ".venv"

Remove-Item -LiteralPath $publishRoot,$launcherPayload,$pyinstallerWork -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $launcherZip,$launcherSha,$installerExe -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publishRoot,$launcherPayload,$pyinstallerWork | Out-Null

# Pin the launcher version in a generated file so the source tree stays clean.
$versionPy = Join-Path $pyinstallerWork "version.py"
$versionLiteral = '__version__ = "' + $ProductVersion + '"'
Set-Content -LiteralPath $versionPy -Value $versionLiteral -Encoding utf8

& $dotnet publish (Join-Path $repo "WebAppLauncher\WebAppLauncher.csproj") -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:Version=$ProductVersion -o (Join-Path $publishRoot "WebAppLauncher")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet publish (Join-Path $repo "WebAppLauncher.Cli\WebAppLauncher.Cli.csproj") -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o (Join-Path $publishRoot "WebAppLauncher.Cli")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item -Path (Join-Path $publishRoot "WebAppLauncher\*") -Destination $launcherPayload -Recurse -Force
Copy-Item -LiteralPath (Join-Path $publishRoot "WebAppLauncher.Cli\WebAppLauncher.Cli.exe") -Destination $launcherPayload -Force

Compress-Archive -Path (Join-Path $launcherPayload "*") -DestinationPath $launcherZip -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $launcherZip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $launcherZip)" | Set-Content -LiteralPath $launcherSha -Encoding ascii

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
$pyInstallerLog = Join-Path $artifacts "pyinstaller.log"
& $venvPython -m PyInstaller `
    --noconfirm `
    --clean `
    --onefile `
    --windowed `
    --name "WebAppLauncher-Setup-v$ProductVersion" `
    --distpath $artifacts `
    --workpath (Join-Path $pyinstallerWork "work") `
    --specpath (Join-Path $pyinstallerWork "spec") `
    --icon (Join-Path $assets "installer.ico") `
    --add-data "$(Join-Path $assets "logo.png");assets" `
    --add-data "$(Join-Path $assets "installer.ico");assets" `
    --add-data "$versionPy;." `
    (Join-Path $PSScriptRoot "installer.py") `
    *>&1 | Tee-Object -FilePath $pyInstallerLog
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not (Test-Path -LiteralPath $installerExe)) {
    throw "PyInstaller output was not found: $installerExe"
}

Write-Host "Built launcher payload:    $launcherZip"
Write-Host "Built launcher checksum:   $launcherSha"
Write-Host "Built Setup.exe:           $installerExe"
Write-Host "Upload the launcher ZIP, checksum, and Setup.exe to the v$ProductVersion GitHub release."
