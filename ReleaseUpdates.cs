using System.Net.Http.Json;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Corrector;

internal static class ReleaseUpdates
{
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    internal static readonly Version Installed = typeof(ReleaseUpdates).Assembly.GetName().Version!;
    internal sealed record Asset(string Name, long Size, string? Digest);
    internal sealed record Update(Version Version, Asset Installer, Asset Signature)
    {
        internal byte[] SignatureBytes { get; init; } = [];
    }
    sealed record Release([property: JsonPropertyName("tag_name")] string? Tag, Asset[]? Assets, bool Draft, bool Prerelease);
    const int SignatureSize = 64;
    static string InstallerName(Version version) => $"Corrector-Setup-{version}-win-x64.exe";
    static string AssetUrl(Version version, bool signature) =>
        $"https://github.com/mrrepac/Corrector/releases/download/v{version}/{InstallerName(version)}" + (signature ? ".sig" : "");

    internal static Version ParseTag(string? tag)
    {
        if (tag is null || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) ||
            version.Build < 0 || version.Revision >= 0 || tag != $"v{version}")
            throw new InvalidDataException("Не удалось прочитать версию релиза.");
        return version;
    }

    internal static Update? Select(string? tag, Asset[]? assets, Version installed)
    {
        var version = ParseTag(tag);
        if (version <= installed) return null;
        Asset Require(string name)
        {
            var matching = assets?.Where(asset => asset?.Name == name).ToArray();
            if (matching is not [var asset] || asset.Digest is not { Length: 71 } digest ||
                !digest.StartsWith("sha256:", StringComparison.Ordinal) || !digest[7..].All(char.IsAsciiHexDigit))
                throw new InvalidDataException($"В релизе отсутствует проверяемый файл {name}.");
            return asset;
        }
        string name = InstallerName(version);
        var installer = Require(name);
        var signature = Require(name + ".sig");
        if (installer.Size is <= 0 or > 512L * 1024 * 1024 || signature.Size != SignatureSize)
            throw new InvalidDataException("Некорректный размер установщика или подписи обновления.");
        return new(version, installer, signature);
    }

    internal static async Task<Update?> Latest(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/mrrepac/Corrector/releases/latest");
        request.Headers.UserAgent.ParseAdd($"Corrector/{Installed.ToString(3)}");
        using var response = await Client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<Release>(cancellationToken: token);
        if (release is { Draft: true } or { Prerelease: true }) return null;
        var update = Select(release?.Tag, release?.Assets, Installed);
        if (update == null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var signatureResponse = await Client.GetAsync(AssetUrl(update.Version, signature: true),
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        signatureResponse.EnsureSuccessStatusCode();
        using var input = await signatureResponse.Content.ReadAsStreamAsync(timeout.Token);
        using var bytes = new MemoryStream(SignatureSize);
        await Assets.CopyBounded(input, bytes, SignatureSize, timeout.Token);
        update = update with { SignatureBytes = bytes.ToArray() };
        VerifySignature(update, update.Installer.Size, update.Installer.Digest![7..], ReleaseSigningKey.PublicKeySpki);
        return update;
    }

    internal static void VerifySignature(Update update, long size, string sha256, string publicKeySpki)
    {
        var signature = update.SignatureBytes;
        if (signature.Length != SignatureSize || update.Signature.Size != SignatureSize ||
            !string.Equals(update.Signature.Digest, "sha256:" + Convert.ToHexString(SHA256.HashData(signature)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольная сумма или длина подписи обновления не совпала.");
        if (update.Version.Build < 0 || update.Version.Revision >= 0 || update.Installer.Name != InstallerName(update.Version) ||
            update.Signature.Name != update.Installer.Name + ".sig" || size is <= 0 or > 512L * 1024 * 1024 ||
            size != update.Installer.Size || sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit) ||
            !string.Equals(update.Installer.Digest, "sha256:" + sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Имя, версия, размер или контрольная сумма установщика не совпали.");
        string message = string.Join('\n', "Corrector update v1", update.Version.ToString(3), update.Installer.Name,
            size.ToString(CultureInfo.InvariantCulture), sha256.ToLowerInvariant());
        using var key = ECDsa.Create();
        byte[] spki = Convert.FromBase64String(publicKeySpki);
        key.ImportSubjectPublicKeyInfo(spki, out int consumed);
        if (consumed != spki.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
            !key.VerifyData(Encoding.UTF8.GetBytes(message), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("Криптографическая подпись релиза Corrector не подтверждена.");
    }

    internal static async Task VerifyPayload(Stream file, Update update, CancellationToken token)
    {
        if (file.Length is <= 0 or > 512L * 1024 * 1024 || file.Length != update.Installer.Size)
            throw new InvalidDataException("Размер установщика не совпал с релизом.");
        file.Position = 0;
        var hash = await SHA256.HashDataAsync(file, token);
        VerifySignature(update, file.Length, Convert.ToHexString(hash), ReleaseSigningKey.PublicKeySpki);
    }

    internal static async Task<string> Download(Update update, CancellationToken token)
    {
        string directory = Path.Combine(Paths.State, "updates");
        Directory.CreateDirectory(directory);
        // NSIS may reopen its own executable for the compressed payload after launch.
        // Keep different releases on different paths, including across Windows sessions.
        string target = Path.Combine(directory, InstallerName(update.Version));
        string partial = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        var asset = update.Installer;
        string url = AssetUrl(update.Version, signature: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            {
                using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await Assets.CopyBounded(input, output, asset.Size, timeout.Token);
                await VerifyPayload(output, update, timeout.Token);
            }
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partial, target, overwrite:true);
            // Remove only this running release's previous cached installer.
            string previous = Path.Combine(directory, InstallerName(new Version(Installed.ToString(3))));
            if (previous != target)
            {
                try { File.Delete(previous); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return target;
        }
        finally { File.Delete(partial); }
    }

    internal static async Task VerifyPublisher(string installer, string application, CancellationToken token)
    {
        // An unsigned running PE has no embedded publisher to pin. Any present or
        // malformed certificate table must still pass the existing Windows policy.
        using var applicationFile = new FileStream(application, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var pe = new PEReader(applicationFile);
        var table = pe.PEHeaders.PEHeader?.CertificateTableDirectory ??
            throw new InvalidDataException("Не удалось прочитать исполняемый файл Corrector.");
        if (table.RelativeVirtualAddress == 0 && table.Size == 0) return;
        // Use Windows Authenticode policy (including signed-file integrity and timestamps)
        // and pin the certificate to the running release. Paths are data, never script text.
        var powershell = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
            @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory };
        powershell.Environment["PSModulePath"] = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\Modules");
        powershell.Environment["CorrectorInstaller"] = installer;
        powershell.Environment["CorrectorApplication"] = application;
        foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", """
            $ErrorActionPreference = 'Stop'
            try {
                $app = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $env:CorrectorApplication
                $setup = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $env:CorrectorInstaller
                if ($app.Status -eq 'Valid' -and $setup.Status -eq 'Valid' -and
                    $app.SignatureType -eq 'Authenticode' -and $setup.SignatureType -eq 'Authenticode' -and
                    $app.SignerCertificate.GetRawCertDataString() -ceq $setup.SignerCertificate.GetRawCertDataString()) { exit 0 }
            } catch {}
            exit 1
            """ }) powershell.ArgumentList.Add(arg);
        using var verifier = Process.Start(powershell) ?? throw new IOException("Не удалось проверить издателя.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await verifier.WaitForExitAsync(timeout.Token); }
        finally { if (!verifier.HasExited) verifier.Kill(true); }
        if (verifier.ExitCode != 0) throw new InvalidDataException("Подпись издателя не подтверждена. Автоустановка остановлена.");
    }

    internal static async Task StartInstaller(string installer, Update update, Func<bool> canInstall, CancellationToken token)
    {
        using var locked = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
        await VerifyPayload(locked, update, token);
        if (FileVersionInfo.GetVersionInfo(installer).ProductVersion != update.Version.ToString())
            throw new InvalidDataException("Версия установщика не совпадает с релизом.");
        await VerifyPublisher(installer, Environment.ProcessPath!, token);
        while (!canInstall()) await Task.Delay(500, token);
        token.ThrowIfCancellationRequested();
        string readyName = @"Local\mrrepac.Corrector.UpdateReady." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName, out bool created);
        if (!created) throw new IOException("Не удалось создать событие готовности установщика.");
        // NSIS requires /D last and unquoted even when the directory contains spaces.
        var start = new ProcessStartInfo(installer) {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer)!,
            Arguments = $"/S /UPDATEPID={Environment.ProcessId} /UPDATEREADY={readyName} /D={Path.TrimEndingDirectorySeparator(Paths.Root)}"
        };
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить установщик.");
        await WaitForInstaller(ready, process, canInstall, TimeSpan.FromSeconds(60), token);
    }

    internal static async Task WaitForInstaller(EventWaitHandle ready, Process process, Func<bool> canInstall,
        TimeSpan readyTimeout, CancellationToken token)
    {
        // Cancellation during application shutdown also stops the waiting child if
        // the WinForms message loop is already exiting and no continuation can run.
        void StopInstaller()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) when (process.HasExited) { }
        }
        var cancellation = token.Register(StopInstaller);
        bool handoff = false;
        try
        {
            var watch = Stopwatch.StartNew();
            bool acknowledged = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new IOException($"Установщик завершился до передачи управления (код {process.ExitCode}). Corrector продолжает работу.");
                acknowledged |= ready.WaitOne(0);
                if (acknowledged && canInstall())
                {
                    cancellation.Dispose();
                    token.ThrowIfCancellationRequested();
                    if (process.HasExited)
                        throw new IOException("Готовый установщик завершился до передачи управления. Corrector продолжает работу.");
                    handoff = true;
                    return;
                }
                if (!acknowledged && watch.Elapsed >= readyTimeout)
                    throw new TimeoutException("Установщик не подтвердил готовность. Corrector продолжает работу.");
                await Task.Delay(100, token);
            }
        }
        finally
        {
            cancellation.Dispose();
            if (!handoff) StopInstaller();
        }
    }
}
