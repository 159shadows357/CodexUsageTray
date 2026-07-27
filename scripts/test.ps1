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
    dotnet test .\CodexUsageTray.slnx -c $Configuration
}
finally {
    Pop-Location
}
