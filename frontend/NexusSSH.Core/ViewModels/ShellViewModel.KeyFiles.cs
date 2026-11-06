using System.Collections.ObjectModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// Die Seite „Schlüssel“ der Startseite: die eigenen Schlüsseldateien ansehen, die Passphrase ändern, zwischen den
// Formaten von OpenSSH und PuTTY umwandeln. Gelesen wird ohne Passphrase; eine Passphrase geht nur an den Kern,
// wird hier nicht aufbewahrt und steht in keinem Protokoll.
public sealed partial class ShellViewModel
{
    /// <summary>
    /// Die Schlüsseldateien in <c>~/.ssh</c> und die, die ein Profil oder eine Identität nennt. Wird mit
    /// <see cref="LoadKeyFiles"/> gelesen, wenn die Seite aufgeht – nicht von selbst.
    /// </summary>
    public ObservableCollection<KeyFile> KeyFiles { get; } = [];

    public void LoadKeyFiles()
    {
        // Wer welchen Schlüssel benutzt: Profile mit eigener Angabe, und Identitäten (ihre Profile nennen denselben).
        var users = new List<(string Path, string Name)>();
        users.AddRange(Settings.Identities
            .Where(identity => identity.KeyPath is { Length: > 0 })
            .Select(identity => (identity.KeyPath!, L.Get("KeyUsedByIdentity", identity.Name))));
        users.AddRange(_profiles
            .Where(profile => profile.KeyPath is { Length: > 0 } && profile.IdentityId is null)
            .Select(profile => (profile.KeyPath!, profile.Name)));

        IReadOnlyList<KeyFile> files;
        try
        {
            files = _backend.Profiles.KeyFiles([.. users.Select(user => user.Path).Distinct(StringComparer.Ordinal)]);
        }
        catch (BackendException)
        {
            files = [];
        }

        KeyFiles.Clear();
        foreach (var file in files)
        {
            var names = users.Where(user => IsSameFile(user.Path, file.Path)).Select(user => user.Name).Distinct().ToList();
            KeyFiles.Add(file with { UsedBy = string.Join(", ", names.Take(6)) + (names.Count > 6 ? " …" : string.Empty) });
        }
    }

    // Pfade aus Profilen stehen mal mit `/`, mal mit `\` da; unter Windows zählt die Schreibung nicht.
    private static bool IsSameFile(string left, string right)
        => string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Ändert die Passphrase einer Schlüsseldatei; ohne <paramref name="newPassphrase"/> liegt der Schlüssel danach
    /// unverschlüsselt da. Liefert <c>null</c>, wenn es gelang, sonst den Satz, woran es lag. Gerechnet wird
    /// nebenher – das Ableiten des Schlüssels aus einer Passphrase dauert spürbar.
    /// </summary>
    public async Task<string?> ChangeKeyPassphraseAsync(KeyFile key, string? passphrase, string? newPassphrase)
    {
        try
        {
            await Task.Run(() => _backend.Profiles.ChangeKeyPassphrase(key.Path, passphrase, newPassphrase));
        }
        catch (BackendException e)
        {
            return KeyFailure(e);
        }

        Log(string.IsNullOrEmpty(newPassphrase) ? L.Get("LogKeyPassphraseRemoved", key.Path) : L.Get("LogKeyPassphraseChanged", key.Path));
        LoadKeyFiles();
        return null;
    }

    /// <summary>
    /// Schreibt den Schlüssel in das andere Format (<see cref="KeyFile.OtherFormat"/>), als neue Datei
    /// <paramref name="name"/> im selben Ordner und mit derselben Passphrase. Liefert <c>null</c>, wenn es gelang,
    /// sonst den Satz, woran es lag.
    /// </summary>
    public async Task<string?> ConvertKeyAsync(KeyFile key, string? passphrase, string name)
    {
        name = name.Trim();
        if (!KeyRequest.IsValidName(name))
        {
            return L.Get("KeyNameInvalid");
        }

        string written;
        try
        {
            written = await Task.Run(() => _backend.Profiles.ConvertKey(key.Path, passphrase, key.OtherFormat, name));
        }
        catch (BackendException e)
        {
            return KeyFailure(e);
        }

        Log(L.Get("LogKeyConverted", key.Path, written));
        LoadKeyFiles();
        return null;
    }

    // Was der Kern zu einer Schlüsseldatei sagt, als Satz. Seine eigene Meldung ist englisch und nur die Einzelheit.
    private static string KeyFailure(BackendException e) => e.Code switch
    {
        "wrong_passphrase" => L.Get("KeyWrongPassphrase"),
        "exists" => L.Get("KeyExists"),
        "invalid_name" => L.Get("KeyNameInvalid"),
        "unsupported" => L.Get("KeyUnsupported"),
        "unreadable" => L.Get("KeyUnreadable"),
        "changed" => L.Get("KeyChanged"),
        _ => $"{L.Get("KeyFailed")} ({Format.Visible(e.Message)})",
    };
}
