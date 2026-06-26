param(
    [string]$BundleVersion = "0.1",
    [string]$WebAppRoot = (Join-Path $env:USERPROFILE ".webapp"),
    [string]$LicensesDirectory = (Join-Path $PSScriptRoot "runtime-licenses")
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repo "artifacts"
$stage = Join-Path $artifacts "runtime-bundle"
$zip = Join-Path $artifacts "WAPL-Runtime-v$BundleVersion.zip"
$checksum = "$zip.sha256"

Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zip,$checksum -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null

foreach ($required in @("runtime", "tools")) {
    $source = Join-Path $WebAppRoot $required
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing runtime bundle input: $source"
    }
}

$stagedManifest = Join-Path $stage "runtime-manifest.toml"
$manifestSource = Join-Path $WebAppRoot "runtime-manifest.toml"
if (-not (Test-Path -LiteralPath $manifestSource)) {
    $manifestSource = Join-Path $PSScriptRoot "runtime-manifest.toml"
}
if (-not (Test-Path -LiteralPath $manifestSource)) {
    throw "Missing runtime bundle manifest: $manifestSource"
}

$manifestText = Get-Content -LiteralPath $manifestSource -Raw
$bundleVersionPattern = '(?m)^bundle_version\s*=\s*"[^"]*"\s*$'
if ($manifestText -notmatch $bundleVersionPattern) {
    throw "runtime-manifest.toml does not contain [runtime].bundle_version"
}
$updatedManifest = [regex]::Replace(
    $manifestText,
    $bundleVersionPattern,
    "bundle_version = `"$BundleVersion`""
)
Set-Content -LiteralPath $stagedManifest -Value $updatedManifest -Encoding utf8

if (-not (Test-Path -LiteralPath $LicensesDirectory)) {
    throw "Runtime license directory was not found: $LicensesDirectory"
}

foreach ($requiredLicenseGroup in @("WebAppLauncher", "Python", "Node.js", "Git", "pnpm", "uv")) {
    $licenseGroupPath = Join-Path $LicensesDirectory $requiredLicenseGroup
    if (-not (Test-Path -LiteralPath $licenseGroupPath)) {
        throw "Runtime license directory is missing $requiredLicenseGroup notices."
    }
}

$licenseFileCount = @(Get-ChildItem -LiteralPath $LicensesDirectory -Recurse -File).Count
if ($licenseFileCount -lt 20) {
    throw "Runtime license directory appears incomplete: only $licenseFileCount files found."
}

Copy-Item -LiteralPath $LicensesDirectory -Destination (Join-Path $stage "LICENSES") -Recurse -Force

$sevenZip = Get-Command 7z -ErrorAction SilentlyContinue
if ($sevenZip) {
    Push-Location $WebAppRoot
    try {
        & $sevenZip.Source a -tzip $zip "runtime" "tools" -mx=5 | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "7-Zip failed while adding runtime and tools."
        }
    }
    finally {
        Pop-Location
    }

    Push-Location $stage
    try {
        & $sevenZip.Source a -tzip $zip "runtime-manifest.toml" "LICENSES" -mx=5 | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "7-Zip failed while adding manifest and licenses."
        }
    }
    finally {
        Pop-Location
    }
}
else {
    Copy-Item -LiteralPath (Join-Path $WebAppRoot "runtime") -Destination $stage -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $WebAppRoot "tools") -Destination $stage -Recurse -Force
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
}

$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $zip)" | Set-Content -LiteralPath $checksum -Encoding ascii

Write-Host "Built runtime bundle: $zip"
Write-Host "Built checksum:       $checksum"
