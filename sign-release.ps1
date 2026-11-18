param(
    [Parameter(Mandatory)][string]$File,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$Thumbprint
)
$ErrorActionPreference = 'Stop'
$cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$Thumbprint"
if (!$cert.HasPrivateKey) { throw 'Signing certificate has no private key.' }
$signature = Set-AuthenticodeSignature -LiteralPath $File -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
if ($signature.Status -ne 'Valid') { throw "Signing failed: $($signature.StatusMessage)" }
