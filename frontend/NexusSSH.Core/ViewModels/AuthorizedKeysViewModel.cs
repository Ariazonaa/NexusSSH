using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

/// <summary>
/// Ein Schlüssel aus <c>authorized_keys</c>, wie der Dialog ihn zeigt. <see cref="LocalName"/> nennt die
/// Schlüsseldatei dieses Rechners mit demselben Fingerabdruck; <see cref="IsProfileKey"/> heißt: Es ist der Schlüssel,
/// den das Profil dieser Verbindung benutzt.
/// </summary>
public sealed record AuthorizedKeyItem(ServerKeyFile File, ServerKey Key, string Title, string Details, string LocalName, bool IsProfileKey)
{
    /// <summary>Ob er sich entfernen lässt – eine Zeile, die kein Schlüssel ist, bleibt, wie sie ist.</summary>
    public bool CanRemove => Key.IsReadable;
}

/// <summary>
/// Eine Datei mit Schlüsseln, wie der Dialog sie zeigt: Kopf, Hinweis, Einträge, und die eigenen Schlüssel, die sich
/// ihr noch anhängen lassen (keine, wenn die Datei nicht zu lesen war).
/// </summary>
public sealed record AuthorizedKeyFileItem(ServerKeyFile File, string Title, string Note, IReadOnlyList<AuthorizedKeyItem> Keys, IReadOnlyList<KeyFile> Addable);

/// <summary>
/// „Hinterlegte Schlüssel“: welche Schlüssel sich am Konto einer Verbindung anmelden dürfen (<c>authorized_keys</c>),
/// einzeln entfernen, einen eigenen dazulegen – alles über SFTP im Kern, ohne Befehl (also auch ohne Shell und
/// ohne weiteren Kanal).
/// </summary>
public sealed partial class AuthorizedKeysViewModel(
    IServerMonitor server,
    string serverName,
    IReadOnlyList<KeyFile> localKeys,
    string? profileKeyPath,
    Action<string, LogSeverity> log) : ObservableObject
{
    [ObservableProperty]
    public partial IReadOnlyList<AuthorizedKeyFileItem> Files { get; private set; } = [];

    /// <summary>Warum die Liste nicht da ist; leer, wenn sie es ist.</summary>
    [ObservableProperty]
    public partial string Error { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public string ServerName => serverName;

    /// <summary>Wie viele Schlüssel (lesbare Zeilen) in allen Dateien zusammen stehen.</summary>
    public int Count => Files.Sum(file => file.Keys.Count(key => key.CanRemove));

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var files = await server.AuthorizedKeysAsync();
            Files = [.. files.Select(Describe)];
            Error = string.Empty;
        }
        catch (RemoteFileException e)
        {
            Files = [];
            Error = FailureText.Describe(e.Code, e.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(Count));
        }
    }

    /// <summary>
    /// Was die Rückfrage vor dem Entfernen sagt: deutlicher, wenn es der Schlüssel dieses Profils ist oder der letzte.
    /// </summary>
    public string RemoveWarning(AuthorizedKeyItem item)
        => item.IsProfileKey ? L.Get("AuthorizedRemoveOwn")
            : Count <= 1 ? L.Get("AuthorizedRemoveLast")
            : L.Get("AuthorizedRemoveConfirm");

    /// <summary>Entfernt die Zeile und liest neu. Liefert <c>null</c>, wenn es gelang, sonst den Satz, woran es lag.</summary>
    public async Task<string?> RemoveAsync(AuthorizedKeyItem item)
    {
        if (!item.CanRemove)
        {
            return null;
        }

        IsBusy = true;
        string? failure = null;
        try
        {
            await server.RemoveAuthorizedKeyAsync(item.File, item.Key);
            log(L.Get("LogAuthorizedRemoved", item.Key.Fingerprint, Label(item.Key), serverName), LogSeverity.Info);
        }
        catch (RemoteFileException e)
        {
            failure = e.Code == "changed" ? L.Get("AuthorizedChanged") : FailureText.Describe(e.Code, e.Message);
            log(L.Get("LogAuthorizedNotRemoved", item.Key.Fingerprint, serverName, failure), LogSeverity.Error);
        }

        await LoadAsync();
        return failure;
    }

    /// <summary>
    /// Hängt einen eigenen Schlüssel an <paramref name="file"/> an (über SFTP) und liest neu. Liefert <c>null</c>, wenn es
    /// gelang, sonst den Satz, woran es lag.
    /// </summary>
    public async Task<string?> AddAsync(AuthorizedKeyFileItem file, KeyFile key)
    {
        IsBusy = true;
        string? failure = null;
        try
        {
            await server.AddAuthorizedKeyAsync(file.File, key.PublicKey);
            log(L.Get("LogAuthorizedAdded", key.Name, serverName), LogSeverity.Info);
        }
        catch (RemoteFileException e)
        {
            failure = e.Code == "changed" ? L.Get("AuthorizedChanged") : FailureText.Describe(e.Code, e.Message);
            log(L.Get("LogKeyNotInstalled", key.Path, serverName, failure), LogSeverity.Error);
        }

        await LoadAsync();
        return failure;
    }

    private AuthorizedKeyFileItem Describe(ServerKeyFile file)
    {
        var notes = new List<string>();
        if (file.Shared)
        {
            notes.Add(L.Get("AuthorizedShared"));
        }

        notes.Add(file.Error switch
        {
            "permission_denied" => L.Get("AuthorizedUnreadableFile"),
            "too_large" => L.Get("AuthorizedTooLarge"),
            "too_many" => L.Get("AuthorizedTooMany", file.Keys.Count),
            { Length: > 0 } code => FailureText.Describe(code, null),
            _ when !file.Exists => L.Get("AuthorizedMissing"),
            _ when file.Keys.Count == 0 => L.Get("AuthorizedEmpty"),
            _ => string.Empty,
        });

        // Anhängen geht an eine Datei, die sich lesen ließ oder die es noch nicht gibt – nur, was dort noch nicht steht.
        var present = file.Keys.Select(key => key.Fingerprint).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<KeyFile> addable = file.Error.Length > 0 ? [] : [.. localKeys.Where(key => key.PublicKey.Length > 0 && key.Fingerprint.Length > 0 && !present.Contains(key.Fingerprint))];
        return new AuthorizedKeyFileItem(file, Format.Visible(file.Path), string.Join(" ", notes.Where(note => note.Length > 0)), [.. file.Keys.Select(key => Describe(file, key))], addable);
    }

    private AuthorizedKeyItem Describe(ServerKeyFile file, ServerKey key)
    {
        if (!key.IsReadable)
        {
            return new AuthorizedKeyItem(file, key, L.Get("AuthorizedUnreadableLine", key.Line), key.Unreadable, string.Empty, false);
        }

        var local = localKeys.FirstOrDefault(candidate => candidate.Fingerprint == key.Fingerprint);
        var isProfileKey = local is not null && profileKeyPath is { Length: > 0 } && SamePath(local.Path, profileKeyPath);
        var details = new List<string> { key.Fingerprint };
        if (key.Options.Length > 0)
        {
            details.Add(L.Get("AuthorizedOptions", key.Options));
        }

        return new AuthorizedKeyItem(file, key, Label(key), string.Join("\n", details), local?.Name ?? string.Empty, isProfileKey);
    }

    // „ssh-ed25519 · admin@laptop“, RSA mit Bitzahl.
    private static string Label(ServerKey key)
    {
        var kind = key.Bits is { } bits && !key.Algorithm.Contains("25519", StringComparison.Ordinal) ? $"{key.Algorithm} ({bits} Bit)" : key.Algorithm;
        return key.Comment.Length > 0 ? $"{kind} · {key.Comment}" : kind;
    }

    private static bool SamePath(string left, string right)
        => string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
