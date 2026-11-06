using System.Security.Cryptography;

namespace NexusSSH.Core.Services;

/// <summary>Prüfsummen von Dateien dieses Rechners – zum Vergleich mit denen, die der Server nennt.</summary>
public static class Checksums
{
    /// <summary>SHA-256 einer lokalen Datei als Hex in Kleinbuchstaben, wie der Kern die des Servers liefert.</summary>
    public static async Task<string> OfFileAsync(string path, CancellationToken cancellation = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellation));
    }
}
