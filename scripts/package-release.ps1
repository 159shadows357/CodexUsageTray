[CmdletBinding()]
param(
    [ValidatePattern('^[0-9A-Za-z][0-9A-Za-z._-]*$')]
    [string]$Version = '0.6.3',
    [string]$RuntimePackSource = 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json',
    [string]$CodeSigningThumbprint,
    [string]$TimestampServer = 'http://timestamp.digicert.com',
    [switch]$SkipAppPublish
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot -Parent
$appExecutable = Join-Path $root 'artifacts\publish\win-x64\CodexUsageTray.exe'
$setupProject = Join-Path $root 'src\CodexUsageTray.Setup\CodexUsageTray.Setup.csproj'
$setupPublish = Join-Path $root 'artifacts\setup-publish\win-x64'
$releaseRoot = Join-Path $root 'artifacts\release'
$packageName = "CodexUsageTray-$Version-win-x64"
$stagingDirectory = Join-Path $releaseRoot $packageName
$zipPath = Join-Path $releaseRoot "$packageName.zip"
$zipHashPath = "$zipPath.sha256"
$readme = Join-Path $root 'packaging\README.txt'
$license = Join-Path $root 'LICENSE'
$publishScript = Join-Path $root 'scripts\publish.ps1'
$signScript = Join-Path $root 'scripts\sign-release.ps1'

function Remove-GeneratedPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$AllowedRoot)

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedRoot = [System.IO.Path]::GetFullPath($AllowedRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $requiredPrefix = $resolvedRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove path outside $resolvedRoot`: $resolvedPath"
    }
    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
}

if (-not $SkipAppPublish) {
    & $publishScript -Version $Version -RuntimePackSource $RuntimePackSource
}
if (-not (Test-Path -LiteralPath $appExecutable)) {
    throw 'Published application not found. Run .\scripts\publish.ps1 first.'
}
$appProductVersion = (Get-Item -LiteralPath $appExecutable).VersionInfo.ProductVersion
$appReleaseVersion = ($appProductVersion -split '\+', 2)[0]
if ($appReleaseVersion -ne $Version) {
    throw "Published application version '$appProductVersion' does not match package version '$Version'. Publish the application again or remove -SkipAppPublish."
}
if ($CodeSigningThumbprint) {
    & $signScript -Path $appExecutable -CertificateThumbprint $CodeSigningThumbprint -TimestampServer $TimestampServer
}
if (-not (Test-Path -LiteralPath $readme)) {
    throw "Package README not found: $readme"
}
if (-not (Test-Path -LiteralPath $license)) {
    throw "License not found: $license"
}

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $setupPublish -Parent) -Force | Out-Null
Remove-GeneratedPath -Path $setupPublish -AllowedRoot (Join-Path $root 'artifacts\setup-publish')
Remove-GeneratedPath -Path $stagingDirectory -AllowedRoot $releaseRoot
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
if (Test-Path -LiteralPath $zipHashPath) {
    Remove-Item -LiteralPath $zipHashPath -Force
}

dotnet publish $setupProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=true `
    -p:TrimMode=full `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:Version=$Version `
    -o $setupPublish `
    --source $RuntimePackSource

$setupExecutable = Join-Path $setupPublish 'CodexUsageTray-Setup.exe'
if (-not (Test-Path -LiteralPath $setupExecutable)) {
    throw "Setup publish finished without $setupExecutable"
}
if ($CodeSigningThumbprint) {
    & $signScript -Path $setupExecutable -CertificateThumbprint $CodeSigningThumbprint -TimestampServer $TimestampServer
}

New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
Copy-Item -LiteralPath $setupExecutable -Destination $stagingDirectory
Copy-Item -LiteralPath $appExecutable -Destination $stagingDirectory
Copy-Item -LiteralPath $license -Destination (Join-Path $stagingDirectory 'LICENSE.txt')

$stagedExecutables = @(
    (Join-Path $stagingDirectory 'CodexUsageTray.exe'),
    (Join-Path $stagingDirectory 'CodexUsageTray-Setup.exe')
)
$signatureStatuses = $stagedExecutables | ForEach-Object {
    (Get-AuthenticodeSignature -LiteralPath $_).Status
}
$allSigned = @($signatureStatuses | Where-Object { $_ -eq [System.Management.Automation.SignatureStatus]::Valid }).Count -eq $stagedExecutables.Count
$allUnsigned = @($signatureStatuses | Where-Object { $_ -eq [System.Management.Automation.SignatureStatus]::NotSigned }).Count -eq $stagedExecutables.Count
if (-not $allSigned -and -not $allUnsigned) {
    throw "Application and Setup Authenticode status must match. Found: $($signatureStatuses -join ', ')."
}

$packageReadme = Get-Content -LiteralPath $readme -Raw
if ($allSigned) {
    $packageReadme = $packageReadme.Replace(
        'The executables are not digitally signed. Windows SmartScreen may show a warning.',
        'The executables are digitally signed by Pongsathon.T. Verify the publisher before running Setup.')
}
Set-Content -LiteralPath (Join-Path $stagingDirectory 'README.txt') -Value $packageReadme -Encoding utf8NoBOM

$hashLines = Get-ChildItem -LiteralPath $stagingDirectory -File |
    Where-Object Extension -eq '.exe' |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$hash  $($_.Name)"
    }
Set-Content -LiteralPath (Join-Path $stagingDirectory 'SHA256SUMS.txt') -Value $hashLines -Encoding ascii

Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Set-Content -LiteralPath $zipHashPath -Value "$zipHash  $([System.IO.Path]::GetFileName($zipPath))" -Encoding ascii
Write-Output $zipPath
Write-Output $zipHashPath
