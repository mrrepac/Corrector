using System.Net.Http.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Corrector;

internal static class ReleaseUpdates
{
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    internal static readonly Version Installed = typeof(ReleaseUpdates).Assembly.GetName().Version!;
    internal sealed record Asset(string Name, long Size, string? Digest);
    internal sealed record Update(Version Version, Asset Installer);
    sealed record Release([property: JsonPropertyName("tag_name")] string? Tag, Asset[]? Assets);

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
        var installer = assets?.FirstOrDefault(a => a.Name == $"Corrector-Setup-{version}-win-x64.exe");
        if (installer?.Digest is not { Length: 71 } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !digest[7..].All(char.IsAsciiHexDigit) || installer.Size <= 0)
            throw new InvalidDataException("В релизе отсутствует проверяемый установщик.");
        return new(version, installer);
    }

    internal static async Task<Update?> Latest(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/mrrepac/Corrector/releases/latest");
        request.Headers.UserAgent.ParseAdd($"Corrector/{Installed.ToString(3)}");
        using var response = await Client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<Release>(cancellationToken: token);
        return Select(release?.Tag, release?.Assets, Installed);
    }

    internal static async Task<string> Download(Update update, CancellationToken token)
    {
        string directory = Path.Combine(Paths.State, "updates");
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "Corrector-Setup.exe");
        string partial = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        var asset = update.Installer;
        string url = $"https://github.com/mrrepac/Corrector/releases/download/v{update.Version}/{asset.Name}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            {
                await response.Content.CopyToAsync(output, timeout.Token);
                output.Position = 0;
                if (output.Length != asset.Size || !string.Equals(asset.Digest,
                    "sha256:" + Convert.ToHexString(await SHA256.HashDataAsync(output, timeout.Token)), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Контрольная сумма установщика не совпала.");
            }
            File.Move(partial, target, overwrite:true);
            return target;
        }
        finally { File.Delete(partial); }
    }

    internal static void StartInstaller(string installer)
    {
        // NSIS requires /D last and unquoted even when the directory contains spaces.
        var start = new ProcessStartInfo(installer) {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer)!,
            Arguments = $"/S /UPDATEPID={Environment.ProcessId} /D={Path.TrimEndingDirectorySeparator(Paths.Root)}"
        };
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить установщик.");
    }
}
