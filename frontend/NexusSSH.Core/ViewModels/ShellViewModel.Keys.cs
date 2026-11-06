using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

/// <summary>Was der Dialog „Schlüssel erzeugen“ abfragt: der Dateiname in <c>~/.ssh</c> und, wer will, eine Passphrase.</summary>
/// <param name="UpdateIdentity">
/// Hat das Profil eine Identität: den neuen Schlüssel in sie schreiben (er gilt dann für alle Profile mit ihr),
/// statt das Profil von ihr zu lösen.
/// </param>
public sealed record KeyRequest(string Name, string? Passphrase, bool UpdateIdentity = false)
{
    /// <summary>
    /// Ein Dateiname, kein Pfad – dieselbe Regel wie im Kern (<c>keys.rs</c>), damit der Dialog es gleich sagt:
    /// Buchstaben, Ziffern, <c>_</c>, <c>-</c> und <c>.</c>, kein Punkt vorn, keine der Dateien, die in
    /// <c>~/.ssh</c> etwas anderes bedeuten.
    /// </summary>
    public static bool IsValidName(string name)
        => name.Length is > 0 and <= 64
            && name[0] != '.'
            && !name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)
            && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            && name.ToLowerInvariant() is not ("config" or "known_hosts" or "authorized_keys");

    /// <summary>Vorschlag für den Dateinamen: <c>id_ed25519_</c> und der Name des Profils, so weit er sich dafür eignet.</summary>
    public static string SuggestedName(ServerProfile profile)
    {
        var suffix = new string([.. profile.Name.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Take(40)]);
        return suffix.Length > 0 ? $"id_ed25519_{suffix}" : "id_ed25519_nexus";
    }
}

// Schlüssel erzeugen und hinterlegen – was sonst ssh-keygen und ssh-copy-id tun. Der private Schlüssel entsteht im
// Kern und bleibt auf diesem Rechner; die Passphrase geht nur an den Kern und wird hier nicht aufbewahrt.
public sealed partial class ShellViewModel
{
    /// <summary>
    /// Legt ein Schlüsselpaar an, trägt den öffentlichen Teil auf dem Server der Sitzung ein und stellt das Profil
    /// auf den neuen Schlüssel um – in dieser Reihenfolge, und nur so weit, wie es gelingt: Lässt der Server keine
    /// Befehle zu, bleibt das Paar liegen und das Profil, wie es war. <c>false</c>, wenn etwas davon scheitert;
    /// der Grund steht im Protokoll.
    /// </summary>
    public async Task<bool> CreateKeyAsync(SessionViewModel session, KeyRequest request)
    {
        if (session.State != ConnectionState.Connected || !session.Profile.IsSsh || session.Connection.Monitor is not { } server)
        {
            return false;
        }

        GeneratedKey key;
        try
        {
            key = _backend.Profiles.GenerateKey(request.Name, request.Passphrase, $"{Environment.UserName}@{Environment.MachineName}");
        }
        catch (BackendException e)
        {
            Log(L.Get("LogKeyNotCreated", request.Name, $"{e.Code}: {e.Message}"), LogSeverity.Error);
            return false;
        }

        try
        {
            await server.InstallKeyAsync(key.PublicKey);
        }
        catch (RemoteFileException e)
        {
            Log(L.Get("LogKeyNotInstalled", key.Path, session.Profile.Name, FailureText.Describe(e.Code, e.Message)), LogSeverity.Error);
            return false;
        }

        // Das Profil, wie es jetzt gespeichert ist – die Sitzung kann älter sein als seine letzte Änderung.
        // Der Schlüssel liegt nur auf diesem Server: Ein Profil mit Identität bekommt damit eigene Angaben, statt
        // die Identität – und mit ihr alle anderen Profile – auf einen Schlüssel umzustellen, den dort keiner kennt.
        var profile = _profiles.FirstOrDefault(candidate => candidate.Id == session.Profile.Id) ?? session.Profile;

        // Auf Wunsch gilt der Schlüssel der Identität – dann benutzen ihn alle Profile mit ihr. Hinterlegt ist er
        // trotzdem nur auf diesem Server; das sagt der Dialog, bevor man es wählt.
        if (request.UpdateIdentity && IdentityOf(profile) is { } identity)
        {
            if (SaveIdentity(identity with { KeyPath = key.Path }) is null)
            {
                return false;
            }

            Log(L.Get("LogKeyCreatedForIdentity", key.Path, session.Profile.Name, identity.Name));
            return true;
        }

        if (SaveProfile(profile with { KeyPath = key.Path, IdentityId = null }) is null)
        {
            return false;
        }

        Log(L.Get("LogKeyCreated", key.Path, session.Profile.Name));
        return true;
    }

    /// <summary>Die Identität, von der ein Profil Benutzer und Schlüssel bezieht; <c>null</c>, wenn es eigene Angaben hat.</summary>
    public Identity? IdentityOf(ServerProfile profile)
        => profile.IdentityId is { } id ? Settings.Identities.FirstOrDefault(identity => identity.Id == id) : null;
}
