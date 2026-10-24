param([Parameter(Mandatory)][string]$Version)

$parsedVersion = $null
if ($Version -cnotmatch '^\d{4}\.[1-9]\d?\.[1-9]\d?\.[1-9]\d{0,4}$' -or
    -not [Version]::TryParse($Version, [ref]$parsedVersion) -or
    $parsedVersion.Revision -lt 1 -or $parsedVersion.Revision -gt 65534 -or
    $parsedVersion.ToString(4) -cne $Version) {
    throw 'Version must be year.month.day.patch with a daily patch from 1 through 65534, for example 2026.10.24.1.'
}
try {
    $null = [DateTime]::new($parsedVersion.Major, $parsedVersion.Minor, $parsedVersion.Build)
}
catch {
    throw 'Version must contain a valid calendar date.'
}
