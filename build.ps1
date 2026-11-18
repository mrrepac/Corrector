param([string]$OutputDirectory = 'release')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish Corrector.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory --source https://api.nuget.org/v3/index.json
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
} finally { Pop-Location }
