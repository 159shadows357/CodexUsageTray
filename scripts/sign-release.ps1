[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$CertificateThumbprint,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$certificatePath = "Cert:\CurrentUser\My\$CertificateThumbprint"
$certificate = Get-Item -LiteralPath $certificatePath -ErrorAction Stop
if (-not $certificate.HasPrivateKey) {
    throw "The code-signing certificate does not have an accessible private key."
}
if ($certificate.NotAfter -le (Get-Date)) {
    throw "The code-signing certificate has expired."
}

$signature = Set-AuthenticodeSignature `
    -LiteralPath $resolvedPath `
    -Certificate $certificate `
    -HashAlgorithm SHA256 `
    -TimestampServer $TimestampServer
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "Authenticode signing failed: $($signature.StatusMessage)"
}

Write-Output "Signed: $resolvedPath"
