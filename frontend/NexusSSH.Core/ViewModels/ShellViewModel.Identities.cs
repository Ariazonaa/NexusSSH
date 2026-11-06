using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

// Identitäten: Benutzer und Schlüssel einmal anlegen, in mehreren Profilen verwenden. Sie stehen in den
// Einstellungen dieses Rechners; im Profil steht nur die Kennung. Der Kern kennt Identitäten nicht – er verbindet
// mit dem Benutzer und dem Schlüssel des Profils, und die schreibt die App bei jeder Änderung dorthin.
public sealed partial class ShellViewModel
{
    /// <summary>Die Identitäten, nach Namen.</summary>
    public IReadOnlyList<Identity> Identities
        => BindableList.Of(Settings.Identities.OrderBy(identity => identity.Name, StringComparer.CurrentCultureIgnoreCase));

    /// <summary>Wie viele Profile eine Identität benutzen – für die Rückfrage vor dem Löschen.</summary>
    public int ProfilesUsing(Identity identity) => _profiles.Count(profile => profile.IdentityId == identity.Id);

    /// <summary>
    /// Legt eine Identität an oder ändert sie und schreibt Benutzer und Schlüssel in jedes Profil, das sie benutzt.
    /// Liefert die gespeicherte Identität (mit Kennung), oder <c>null</c>, wenn Name oder Benutzer nicht taugen.
    /// </summary>
    public Identity? SaveIdentity(Identity identity)
    {
        var (name, user, key) = (identity.Name.Trim(), identity.User.Trim(), identity.KeyPath?.Trim().Trim('"'));
        if (name.Length == 0 || !Identity.IsValidUser(user))
        {
            return null;
        }

        var saved = identity with
        {
            Id = identity.Id.Length > 0 ? identity.Id : Guid.NewGuid().ToString("N"),
            Name = name,
            User = user,
            KeyPath = string.IsNullOrEmpty(key) ? null : key,
        };
        Settings.Identities = [.. Settings.Identities.Where(other => other.Id != saved.Id), saved];
        OnPropertyChanged(nameof(Identities));

        foreach (var profile in _profiles.Where(profile => profile.IdentityId == saved.Id).ToList())
        {
            SaveProfile(profile);
        }

        return saved;
    }

    /// <summary>Entfernt eine Identität. Die Profile, die sie benutzten, behalten Benutzer und Schlüssel als eigene Angaben.</summary>
    public void DeleteIdentity(Identity identity)
    {
        Settings.Identities = [.. Settings.Identities.Where(other => other.Id != identity.Id)];
        OnPropertyChanged(nameof(Identities));

        foreach (var profile in _profiles.Where(profile => profile.IdentityId == identity.Id).ToList())
        {
            SaveProfile(profile);
        }
    }

    // Was an den Kern geht: Benutzer und Schlüssel aus der Identität des Profils. Gibt es die nicht (mehr), ist das
    // Profil wieder eines mit eigenen Angaben.
    private ServerProfile WithIdentity(ServerProfile profile)
    {
        if (profile.IdentityId is null)
        {
            return profile;
        }

        return Settings.Identities.FirstOrDefault(identity => identity.Id == profile.IdentityId) is { } identity
            ? profile with { User = identity.User, KeyPath = identity.KeyPath, UseAgent = identity.UseAgent }
            : profile with { IdentityId = null };
    }
}
