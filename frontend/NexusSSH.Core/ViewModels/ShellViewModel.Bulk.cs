using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.ViewModels;

// Mehrere Server der Startseite auf einmal: verbinden, in eine Gruppe verschieben, Schlagworte ändern, löschen.
// Jede Änderung geht wie eine einzelne durch SaveProfile bzw. DeleteProfile – der Kern prüft jedes Profil.
public sealed partial class ShellViewModel
{
    /// <summary>Mehr Sitzungen öffnet ein einzelner Klick nicht – ein Versehen mit hundert markierten Servern soll keines werden.</summary>
    public const int MaxConnectAtOnce = 12;

    /// <summary>Öffnet zu jedem der Profile eine Sitzung, höchstens <see cref="MaxConnectAtOnce"/>. Liefert, wie viele es wurden.</summary>
    public int ConnectAll(IEnumerable<ServerProfile> profiles)
    {
        var chosen = Stored(profiles);
        foreach (var profile in chosen.Take(MaxConnectAtOnce))
        {
            OpenSession(profile);
        }

        if (chosen.Count > MaxConnectAtOnce)
        {
            Log(L.Get("LogBulkConnectLimit", MaxConnectAtOnce), LogSeverity.Warning);
        }

        return Math.Min(chosen.Count, MaxConnectAtOnce);
    }

    /// <summary>Legt die Profile in eine Gruppe (leer = ohne Gruppe). Liefert, wie viele sich dadurch geändert haben.</summary>
    public int MoveToGroup(IEnumerable<ServerProfile> profiles, string? group)
    {
        var target = group?.Trim() ?? string.Empty;
        var moved = Stored(profiles).Count(profile => profile.Group != target && SaveProfile(profile with { Group = target }) is not null);
        if (moved > 0)
        {
            Log(target.Length > 0 ? L.Get("LogBulkMoved", moved, Format.Visible(target)) : L.Get("LogBulkUngrouped", moved));
        }

        return moved;
    }

    /// <summary>
    /// Gibt den Profilen Schlagworte dazu und nimmt ihnen andere – beides als Text, durch Komma getrennt. Liefert,
    /// wie viele sich dadurch geändert haben.
    /// </summary>
    public int ChangeTags(IEnumerable<ServerProfile> profiles, string? add, string? remove)
    {
        var (adding, removing) = (ParseTags(add), ParseTags(remove));
        var changed = 0;
        foreach (var profile in Stored(profiles))
        {
            List<string> tags = [.. profile.Tags.Where(tag => !removing.Contains(tag, StringComparer.CurrentCultureIgnoreCase))];
            tags.AddRange(adding.Where(tag => !tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase)));
            if (!tags.SequenceEqual(profile.Tags) && SaveProfile(profile with { Tags = tags }) is not null)
            {
                changed++;
            }
        }

        if (changed > 0)
        {
            Log(L.Get("LogBulkTagged", changed));
        }

        return changed;
    }

    /// <summary>Entfernt die Profile samt gespeicherter Geheimnisse. Offene Sitzungen dazu bleiben bestehen.</summary>
    public int DeleteProfiles(IEnumerable<ServerProfile> profiles)
    {
        var chosen = Stored(profiles);
        foreach (var profile in chosen)
        {
            DeleteProfile(profile);
        }

        if (chosen.Count > 0)
        {
            Log(L.Get("LogBulkDeleted", chosen.Count));
        }

        return chosen.Count;
    }

    /// <summary>Schlagworte aus einem Text, wie im Profil-Editor: durch Komma oder Semikolon getrennt, ohne Doppelte.</summary>
    public static IReadOnlyList<string> ParseTags(string? text)
        => [.. (text ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.CurrentCultureIgnoreCase)];

    // Jedes Profil einmal (auf der Startseite steht ein Favorit zweimal), und so, wie es jetzt gespeichert ist – die
    // Auswahl kann älter sein als seine letzte Änderung. Was es nicht mehr gibt, fällt weg.
    private List<ServerProfile> Stored(IEnumerable<ServerProfile> profiles)
        => [.. profiles.Select(profile => profile.Id).Distinct().Select(id => _profiles.FirstOrDefault(profile => profile.Id == id)).OfType<ServerProfile>()];
}
