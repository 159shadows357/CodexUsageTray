[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$programsDirectory = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$installDirectory = [System.IO.Path]::GetFullPath((Join-Path $programsDirectory 'CodexUsageTray'))
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

if (-not $installDirectory.StartsWith($programsDirectory + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove unexpected path: $installDirectory"
}

if (Test-Path -LiteralPath $installExecutable) {
    $stopRequest = Start-Process -FilePath $installExecutable -ArgumentList '--exit' -WindowStyle Hidden -PassThru -ErrorAction SilentlyContinue
    if ($stopRequest) { $stopRequest | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Get-InstalledProcess) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
}

$remaining = Get-InstalledProcess
if ($remaining) {
    throw 'Codex Usage Tray is still running. Exit it from the tray menu and run uninstall again.'
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Test-Path $runKey) {
    Remove-ItemProperty -Path $runKey -Name 'CodexUsageTray' -ErrorAction SilentlyContinue
}

if ((Test-Path -LiteralPath $installDirectory) -and $PSCmdlet.ShouldProcess($installDirectory, 'Remove current-user installation')) {
    $removeDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        try {
            Remove-Item -LiteralPath $installDirectory -Recurse -Force
            break
        }
        catch [System.IO.IOException] {
            if ([DateTime]::UtcNow -ge $removeDeadline) { throw }
            Start-Sleep -Milliseconds 250
        }
        catch [System.UnauthorizedAccessException] {
            if ([DateTime]::UtcNow -ge $removeDeadline) { throw }
            Start-Sleep -Milliseconds 250
        }
    } while (Test-Path -LiteralPath $installDirectory)
}

Write-Output 'Codex Usage Tray uninstalled. Runtime data under %LOCALAPPDATA%\CodexUsageTray was kept.'
