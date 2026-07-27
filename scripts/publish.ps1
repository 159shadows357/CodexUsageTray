[CmdletBinding()]
param(
    [ValidatePattern('^[0-9A-Za-z][0-9A-Za-z._-]*$')]
    [string]$Version = '0.4.0',
    [string]$RuntimePackSource = 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\CodexUsageTray\CodexUsageTray.csproj'
$output = Join-Path $root 'artifacts\publish\win-x64'
New-Item -ItemType Directory -Path $output -Force | Out-Null

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:Version=$Version `
    -o $output `
    --source $RuntimePackSource

$executable = Join-Path $output 'CodexUsageTray.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Publish finished without $executable"
}
Write-Output $executable
