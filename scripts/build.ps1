[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet restore .\CodexUsageTray.slnx
    dotnet build .\CodexUsageTray.slnx -c $Configuration --no-restore
}
finally {
    Pop-Location
}
