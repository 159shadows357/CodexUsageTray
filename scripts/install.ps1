[CmdletBinding()]
param(
    [switch]$StartWithWindows,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root 'artifacts\publish\win-x64'
$sourceExecutable = Join-Path $source 'CodexUsageTray.exe'
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\CodexUsageTray'
$installExecutable = Join-Path $installDirectory 'CodexUsageTray.exe'

function Get-InstalledProcess {
    @(Get-Process -Name 'CodexUsageTray' -ErrorAction SilentlyContinue | Where-Object {
        try {
            [System.IO.Path]::GetFullPath($_.Path).Equals(
                [System.IO.Path]::GetFullPath($installExecutable),
                [System.StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $false
        }
    })
}

if (-not (Test-Path -LiteralPath $sourceExecutable)) {
    throw 'Published executable not found. Run .\scripts\publish.ps1 first.'
}

if (Test-Path -LiteralPath $installExecutable) {
    $stopRequest = Start-Process -FilePath $installExecutable -ArgumentList '--exit' -WindowStyle Hidden -PassThru -ErrorAction SilentlyContinue
    if ($stopRequest) { $stopRequest | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Get-InstalledProcess) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (Get-InstalledProcess) {
        throw 'Codex Usage Tray did not exit within 10 seconds. Exit it from the tray menu and retry.'
    }
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $installDirectory -Recurse -Force

if ($StartWithWindows) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $runKey -Force | Out-Null
    New-ItemProperty -Path $runKey -Name 'CodexUsageTray' -Value ('"{0}" --autostart' -f $installExecutable) -PropertyType String -Force | Out-Null
}

if (-not $NoLaunch) {
    Start-Process -FilePath $installExecutable -WorkingDirectory $installDirectory -WindowStyle Hidden
}

Write-Output "Installed to $installDirectory"
