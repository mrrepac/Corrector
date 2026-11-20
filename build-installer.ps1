param(
    [string]$PortableZip,
    [string]$Compiler,
    [string]$SigningThumbprint,
    [switch]$SignUpdate
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
[xml]$project = Get-Content -Raw -LiteralPath (Join-Path $root 'Corrector.csproj')
$version = $project.Project.PropertyGroup.Version
if (!$Compiler) {
    $Compiler = (Get-Command makensis.exe -ErrorAction SilentlyContinue).Source
    if (!$Compiler) {
        $candidates = @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe")
        $cache = Join-Path $env:LOCALAPPDATA 'electron-builder\Cache\nsis'
        if (Test-Path -LiteralPath $cache) {
            $candidates += @(Get-ChildItem $cache -Directory | Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName 'makensis.exe' })
        }
        $Compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if (!$Compiler) { throw 'Установите NSIS 3 или укажите путь к makensis.exe через -Compiler.' }
$dist = Join-Path $root 'dist'
# Stage on the output volume so the completed installer can be replaced atomically.
$base = [IO.Path]::GetFullPath($dist)
$work = Join-Path $base ("Corrector-Setup-" + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'payload'
$output = Join-Path $dist "Corrector-Setup-$version-win-x64.exe"
New-Item -ItemType Directory -Path $payload -Force | Out-Null
try {
    if ($PortableZip) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $PortableZip).Path, $payload)
    } else {
        & (Join-Path $root 'build.ps1') -OutputDirectory $payload -SigningThumbprint $SigningThumbprint
    }

    $exe = Get-Item -LiteralPath (Join-Path $payload 'Corrector.exe')
    if ($SigningThumbprint) {
        $signature = Get-AuthenticodeSignature -LiteralPath $exe.FullName
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $SigningThumbprint) {
            throw 'Application signature does not match the installer signing certificate.'
        }
    }
    if ($exe.VersionInfo.ProductVersion.Split('+')[0] -ne $version) { throw 'Версия EXE не совпадает с проектом.' }
    if (!(Test-Path -LiteralPath (Join-Path $payload 'assets.lock.json'))) { throw 'Отсутствует assets.lock.json.' }
    $manifest = @('!macro Payload Action')
    foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName) {
        $name = $file.FullName.Substring($payload.Length + 1)
        if ($name -notmatch '^(Corrector\.exe|assets\.lock\.json|[\w.-]+\.dll|README\.md|THIRD_PARTY\.md|licenses\\[\w.-]+\.txt)$') {
            throw "Лишний файл в переносимой сборке: $name"
        }
        $manifest += ('  !insertmacro ${Action} "' + $name + '"')
    }
    $manifest += '!macroend'
    $list = Join-Path $work 'payload.nsh'
    [IO.File]::WriteAllLines($list, [string[]]$manifest)
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    $built = Join-Path $work ([IO.Path]::GetFileName($output))
    & $Compiler /V2 "/DVersion=$version" "/DPayloadDir=$payload" "/DFileList=$list" "/DSetupExe=$built" (Join-Path $root 'installer\Corrector.nsi')
    if ($LASTEXITCODE -ne 0) { throw "Ошибка NSIS: $LASTEXITCODE" }
    if ($SigningThumbprint) { & "$root/sign-release.ps1" -File $built -Thumbprint $SigningThumbprint }
    if ($SignUpdate) { & "$root/sign-update.ps1" -Installer $built | Out-Null }

    $publishLock = [Threading.Mutex]::new($false, 'Local\mrrepac.Corrector.BuildInstaller')
    try {
        try { $acquired = $publishLock.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (!$acquired) { throw 'Another build is publishing an installer; retry the build.' }
        try {
            # Invalidate any old signature before replacing its installer. A local
            # unsigned rebuild of the same version must not inherit a release signature.
            [IO.File]::Delete("$output.sig")
            if ([IO.File]::Exists($output)) { [IO.File]::Replace($built, $output, [NullString]::Value) }
            else { [IO.File]::Move($built, $output) }
            if ($SignUpdate) { [IO.File]::Move("$built.sig", "$output.sig") }
        } finally { $publishLock.ReleaseMutex() }
    } finally { $publishLock.Dispose() }
    Write-Output $output
    if ($SignUpdate) { Write-Output "$output.sig" }
} finally {
    $resolved = (Resolve-Path -LiteralPath $work).Path
    if ([IO.Path]::GetDirectoryName($resolved) -eq $base.TrimEnd('\') -and
        [IO.Path]::GetFileName($resolved) -match '^Corrector-Setup-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
