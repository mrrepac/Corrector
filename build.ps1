param([string]$OutputDirectory = 'release', [string]$SigningThumbprint)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish Corrector.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory --source https://api.nuget.org/v3/index.json
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $metadata = Get-Content -Raw 'bin/Release/net10.0-windows/win-x64/Corrector.deps.json' | ConvertFrom-Json
    foreach ($component in 'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') {
        $prefix = "runtimepack.$component.Runtime.win-x64/"
        $pack = @($metadata.libraries.PSObject.Properties.Name | Where-Object { $_.StartsWith($prefix) })
        if ($pack.Count -ne 1) { throw "Missing runtime pack: $component" }
        $version = [version]$pack[0].Substring($prefix.Length)
        if ($version.Major -ne 10 -or $version -lt [version]'10.0.15') { throw "Unexpected runtime version: $component $version" }
        Write-Host "$component $version"
    }
    if ($SigningThumbprint) {
        & "$PSScriptRoot/sign-release.ps1" -File (Join-Path $OutputDirectory 'Corrector.exe') -Thumbprint $SigningThumbprint
    }
} finally { Pop-Location }
