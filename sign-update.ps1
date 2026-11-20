#Requires -Version 7.2
param([Parameter(Mandatory)][string]$Installer)

$ErrorActionPreference = 'Stop'
$file = Get-Item -LiteralPath $Installer
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ReleaseSigningKey.cs'))
$pins = [regex]::Matches($source, '\bconst\s+string\s+PublicKeySpki\s*=\s*"([A-Za-z0-9+/]+={0,2})"\s*;')
if ($pins.Count -ne 1) { throw 'Expected exactly one PublicKeySpki constant in ReleaseSigningKey.cs.' }
$pinnedSpki = $pins[0].Groups[1].Value

$installerStream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'Read')
$key = $signer = $publicVerifier = $null
$signaturePath = $file.FullName + '.sig'
$temporary = $signaturePath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
try {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName).ProductVersion
    if ($version -cnotmatch '\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z' -or
        $file.Name -cne "Corrector-Setup-$version-win-x64.exe") {
        throw 'The installer filename must match its exact three-part ProductVersion.'
    }
    if ($installerStream.Length -le 0 -or $installerStream.Length -gt 512MB) {
        throw 'Installer size is outside the supported update range.'
    }

    $keyName = 'Corrector.ReleaseSigning.v1'
    $provider = [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
    $options = [Security.Cryptography.CngKeyOpenOptions]::None
    if (![Security.Cryptography.CngKey]::Exists($keyName, $provider, $options)) {
        throw "The current user's release signing key '$keyName' is missing."
    }
    $key = [Security.Cryptography.CngKey]::Open($keyName, $provider, $options)
    if ($key.AlgorithmGroup -ne [Security.Cryptography.CngAlgorithmGroup]::ECDsa) {
        throw 'Expected an ECDSA release signing key.'
    }
    $signer = [Security.Cryptography.ECDsaCng]::new($key)
    $publicParameters = $signer.ExportParameters($false)
    if ($publicParameters.Curve.Oid.Value -ne '1.2.840.10045.3.1.7' -or
        [Convert]::ToBase64String($signer.ExportSubjectPublicKeyInfo()) -cne $pinnedSpki) {
        throw 'The release signing key does not match the pinned P-256 public key.'
    }

    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $digest = [Convert]::ToHexString($hasher.ComputeHash($installerStream)).ToLowerInvariant() }
    finally { $hasher.Dispose() }
    $size = $installerStream.Length.ToString([Globalization.CultureInfo]::InvariantCulture)
    $message = [Text.Encoding]::UTF8.GetBytes((@('Corrector update v1', $version, $file.Name, $size, $digest) -join [char]10))
    $hashAlgorithm = [Security.Cryptography.HashAlgorithmName]::SHA256
    $format = [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation
    $signature = $signer.SignData($message, $hashAlgorithm, $format)

    [IO.File]::WriteAllBytes($temporary, $signature)
    $written = [IO.File]::ReadAllBytes($temporary)
    $publicVerifier = [Security.Cryptography.ECDsa]::Create()
    $publicVerifier.ImportParameters($publicParameters)
    if ($written.Length -ne 64 -or !$publicVerifier.VerifyData($message, $written, $hashAlgorithm, $format)) {
        throw 'Update signature verification failed.'
    }
    [IO.File]::Move($temporary, $signaturePath, $true)
    Write-Output $signaturePath
} finally {
    if ($publicVerifier) { $publicVerifier.Dispose() }
    if ($signer) { $signer.Dispose() }
    if ($key) { $key.Dispose() }
    $installerStream.Dispose()
    [IO.File]::Delete($temporary)
}
