using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Corrector;

internal static class ReleaseUpdates
{
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    internal static readonly Version Installed = typeof(ReleaseUpdates).Assembly.GetName().Version!;
    internal const string ReleasesUrl = "https://github.com/mrrepac/Corrector/releases/tag/";

    sealed record Release([property: JsonPropertyName("tag_name")] string? Tag);

    internal static Version ParseTag(string? tag)
    {
        if (tag is null || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) ||
            version.Build < 0 || version.Revision >= 0 || tag != $"v{version}")
            throw new InvalidDataException("Не удалось прочитать версию релиза.");
        return version;
    }

    internal static async Task<Version> Latest(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/mrrepac/Corrector/releases/latest");
        request.Headers.UserAgent.ParseAdd($"Corrector/{Installed.ToString(3)}");
        using var response = await Client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<Release>(cancellationToken: token);
        return ParseTag(release?.Tag);
    }
}
