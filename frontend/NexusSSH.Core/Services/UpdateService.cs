using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusSSH.Core.Services;

/// <summary>Eine Version, die neuer ist als die laufende. <see cref="Url"/> führt zur Release-Seite.</summary>
public sealed record UpdateInfo(string Version, string Name, string Url);

/// <summary>
/// Sucht an einer Release-Quelle nach einer neueren Version. Wirft nie: Offline, Zeitüberschreitung oder eine
/// unlesbare Antwort bedeuten „kein Update" – eine Update-Prüfung darf die App nicht stören.
/// </summary>
public interface IUpdateService
{
    Task<UpdateInfo?> CheckAsync(Version current, CancellationToken cancellationToken = default);
}

/// <summary>
/// Liest das neueste Release eines öffentlichen GitHub-Repos (ohne Anmeldung). Schickt nur eine GET-Anfrage an
/// <c>api.github.com</c> – kein Konto, keine Daten des Benutzers.
/// </summary>
public sealed class GitHubUpdateService(HttpClient http, string repository) : IUpdateService
{
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
            // GitHub verlangt einen User-Agent; sonst 403.
            request.Headers.UserAgent.ParseAdd("NexusSSH-UpdateCheck");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return UpdateCheck.Parse(json, current);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>Der reine Teil: aus der Release-Antwort von GitHub ein <see cref="UpdateInfo"/> machen, wenn sie neuer ist.</summary>
public static class UpdateCheck
{
    /// <summary>
    /// <c>null</c>, wenn das JSON unlesbar ist, es ein Entwurf oder eine Vorabversion ist, oder die Version nicht
    /// neuer als <paramref name="current"/> ist.
    /// </summary>
    public static UpdateInfo? Parse(string json, Version current)
    {
        GitHubRelease? release;
        try
        {
            release = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.GitHubRelease);
        }
        // Auch ArgumentException: Die wirft der JSON-Leser bei einem halben UTF-16-Zeichen im Text (gefunden von den
        // Zufallstests) – für den Aufrufer ist das dieselbe unlesbare Eingabe.
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return null;
        }

        if (release is null || release.Draft || release.Prerelease || string.IsNullOrWhiteSpace(release.TagName))
        {
            return null;
        }

        if (ParseVersion(release.TagName) is not { } latest || latest <= Normalize(current))
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name;
        return new UpdateInfo(latest.ToString(3), name, release.HtmlUrl ?? string.Empty);
    }

    /// <summary>Liest die Version aus einem Tag wie <c>v0.21.0</c> oder <c>0.21</c>; <c>null</c> ohne <c>X.Y</c> davor.</summary>
    public static Version? ParseVersion(string tag)
    {
        var span = tag.AsSpan().Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            span = span[1..];
        }

        // Nur die führende Zahl (major.minor[.patch]); alles ab einem Vorab-Zusatz (-rc1) oder Metadaten (+abc) weg.
        var end = 0;
        while (end < span.Length && (char.IsAsciiDigit(span[end]) || span[end] == '.'))
        {
            end++;
        }

        return Version.TryParse(span[..end], out var version) ? Normalize(version) : null;
    }

    // Auf drei Stellen bringen, damit der Vergleich nicht an einer gesetzten/nicht gesetzten Revision hängt.
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), 0);
}

/// <summary>Die Felder von <c>releases/latest</c>, die wir brauchen.</summary>
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
