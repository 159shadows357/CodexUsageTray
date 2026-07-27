[CmdletBinding()]
param(
    [Parameter(ParameterSetName = 'Enable', Mandatory)]
    [switch]$Enable,
    [Parameter(ParameterSetName = 'Disable', Mandatory)]
    [switch]$Disable
)

$ErrorActionPreference = 'Stop'
$installExecutable = Join-Path $env:LOCALAPPDATA 'Programs\CodexUsageTray\CodexUsageTray.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

if ($Enable) {
    if (-not (Test-Path -LiteralPath $installExecutable)) {
        throw "Installed executable not found: $installExecutable"
    }
    New-Item -Path $runKey -Force | Out-Null
    New-ItemProperty -Path $runKey -Name 'CodexUsageTray' -Value ('"{0}" --autostart' -f $installExecutable) -PropertyType String -Force | Out-Null
    Write-Output 'Start with Windows enabled.'
}
else {
    if (Test-Path $runKey) {
        Remove-ItemProperty -Path $runKey -Name 'CodexUsageTray' -ErrorAction SilentlyContinue
    }
    Write-Output 'Start with Windows disabled.'
}
