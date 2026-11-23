using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NexusSSH.Core.Services;

/// <summary>Ein Update ließ sich nicht holen oder nicht starten; <see cref="Code"/> sagt, woran es lag.</summary>
public sealed class UpdateException(string code, string message) : Exception(message)
{
    /// <summary><c>bad_source</c>, <c>http</c>, <c>size</c>, <c>checksum</c>, <c>signature</c>, <c>start</c>.</summary>
    public string Code => code;
}

/// <summary>
/// Welche Datei eines Releases das Setup für diesen Rechner ist – und nur eine, die ganz zu dem passt, was die
/// Releases von NexusSSH haben: Name <c>NexusSSH-Setup-&lt;Version&gt;[-arm64].exe</c>, Adresse unter
/// <c>github.com/&lt;Repo&gt;/releases/download/&lt;Tag&gt;/</c>, eine Größe und ein SHA-256 von GitHub.
/// </summary>
public static class UpdateSetup
{
    /// <summary>Größer ist kein Setup von NexusSSH (heute rund 31 MB).</summary>
    public const long MaxSize = 300L * 1024 * 1024;

    public static UpdateAsset? Choose(UpdateInfo update, string repository, Architecture architecture)
    {
        if (architecture is not (Architecture.X64 or Architecture.Arm64) || update.Tag.Length == 0)
        {
            return null;
        }

        var name = $"NexusSSH-Setup-{update.Version}{(architecture == Architecture.Arm64 ? "-arm64" : string.Empty)}.exe";
        var expected = $"https://github.com/{repository}/releases/download/{Uri.EscapeDataString(update.Tag)}/{name}";
        return update.Assets.FirstOrDefault(asset =>
            asset.Name == name
            && asset.Url.AbsoluteUri == expected
            && asset.Size is > 0 and <= MaxSize);
    }

    /// <summary>Wohin ein Download führen darf: GitHub selbst und seine Server für Dateien, nur über https.</summary>
    public static bool IsAllowedSource(Uri uri)
        => uri is { IsAbsoluteUri: true, Scheme: "https", UserInfo.Length: 0 }
            && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Lädt das Setup eines Updates und prüft es, bevor es als fertige Datei daliegt: Jeder Sprung einer Weiterleitung
/// bleibt bei GitHub, die Größe stimmt aufs Byte, der SHA-256 ist der, den GitHub nennt. Was nicht passt, wird
/// gelöscht – liegen bleibt nur eine geprüfte Datei.
/// </summary>
public sealed class UpdateDownloader(HttpClient http)
{
    private const int MaxRedirects = 5;

    /// <summary>Lädt <paramref name="asset"/> nach <paramref name="directory"/>; liefert den Pfad der geprüften Datei.</summary>
    /// <exception cref="UpdateException">Wenn etwas nicht passt oder der Download scheitert.</exception>
    public async Task<string> DownloadAsync(UpdateAsset asset, string directory, Action<double>? progress, CancellationToken cancellation)
    {
        if (Path.GetFileName(asset.Name) != asset.Name || asset.Name.Length == 0)
        {
            throw new UpdateException("bad_source", asset.Name);
        }

        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, asset.Name);
        var part = target + ".part";
        try
        {
            using var response = await GetAsync(asset.Url, cancellation).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
            {
                throw new UpdateException("size", $"{length} bytes instead of {asset.Size}");
            }

            var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                await using (output.ConfigureAwait(false))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > asset.Size)
                        {
                            throw new UpdateException("size", $"more than {asset.Size} bytes");
                        }

                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                        progress?.Invoke((double)total / asset.Size);
                    }

                    if (total != asset.Size)
                    {
                        throw new UpdateException("size", $"{total} bytes instead of {asset.Size}");
                    }

                    if (Convert.ToHexStringLower(hash.GetHashAndReset()) != asset.Sha256)
                    {
                        throw new UpdateException("checksum", "the SHA-256 differs from the one GitHub names");
                    }
                }
            }

            File.Move(part, target, overwrite: true);
            return target;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            Delete(part);
            throw new UpdateException("http", e.Message);
        }
        catch
        {
            Delete(part);
            throw;
        }
    }

    /// <summary>Räumt weg, was ein früheres Update hinterlassen hat (beim Start; die Datei läuft dann nicht mehr).</summary>
    public static void RemoveStale(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Beim nächsten Start wieder.
        }
    }

    // Folgt Weiterleitungen selbst, damit jeder Sprung geprüft wird. Folgt der HttpClient von sich aus, zählt die
    // Adresse, bei der er ankam.
    private async Task<HttpResponseMessage> GetAsync(Uri start, CancellationToken cancellation)
    {
        var uri = start;
        for (var hop = 0; ; hop++)
        {
            if (!UpdateSetup.IsAllowedSource(uri))
            {
                throw new UpdateException("bad_source", uri.Host);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("NexusSSH-Update");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            {
                response.Dispose();
                if (hop >= MaxRedirects)
                {
                    throw new UpdateException("http", "too many redirects");
                }

                uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                continue;
            }

            if (response.RequestMessage?.RequestUri is { } arrived && !UpdateSetup.IsAllowedSource(arrived))
            {
                response.Dispose();
                throw new UpdateException("bad_source", arrived.Host);
            }

            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new UpdateException("http", $"HTTP {(int)response.StatusCode}");
            }

            return response;
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Bleibt liegen; RemoveStale räumt beim nächsten Start.
        }
    }
}

/// <summary>
/// Was nur Windows kann: ob die App aus einer Installation läuft, und das Setup starten. Ohne Installation (ein
/// Build aus dem Quelltext) gibt es nur den Link zur Release-Seite.
/// </summary>
public interface IUpdateInstaller
{
    /// <summary>Die App läuft aus dem Ordner, den das Setup eingetragen hat – nur dann ersetzt ein Setup sie.</summary>
    bool CanInstall { get; }

    /// <summary>Wohin das Setup geladen wird; beim nächsten Start räumt die App den Ordner weg.</summary>
    string DownloadDirectory { get; }

    /// <summary>
    /// Prüft die Signatur (ist eine da, muss sie gültig sein) und startet das Setup ohne Fragen, im Modus der
    /// bisherigen Installation; danach soll die App sich beenden.
    /// </summary>
    /// <exception cref="UpdateException">Mit <c>signature</c> oder <c>start</c>.</exception>
    void Start(string setupPath);
}
