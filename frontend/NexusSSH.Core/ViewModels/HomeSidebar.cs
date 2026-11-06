namespace NexusSSH.Core.ViewModels;

/// <summary>Ein Eintrag der Seitenleiste auf der Startseite (Entwurf A) – fünf oben, drei unter „Mehr“.</summary>
public enum HomeEntry
{
    Servers,
    Snippets,

    /// <summary>Eine Seite für beide, oben umschaltbar: Schlüsseldateien und Identitäten.</summary>
    KeysAndIdentities,
    Tunnels,

    /// <summary>Kein Ziel: klappt die drei Einträge darunter auf und zu.</summary>
    More,
    Syncs,
    KnownHosts,
    Overview,
}

/// <summary>
/// Die Seitenleiste fasst die Seiten der Startseite (<see cref="HomePage"/>, unverändert) zu fünf Einträgen
/// zusammen: Server, Snippets, Schlüssel &amp; Identitäten, Tunnel und „Mehr“ mit Abgleichen, bekannten Servern und
/// der Übersicht. Jede Seite ist über genau einen Eintrag zu erreichen (Test).
/// </summary>
public static class HomeSidebar
{
    /// <summary>Alle Einträge in der Reihenfolge der Seitenleiste; die unter „Mehr“ stehen direkt dahinter.</summary>
    public static IReadOnlyList<HomeEntry> Entries { get; } = Enum.GetValues<HomeEntry>();

    /// <summary>Was „Mehr“ aufklappt.</summary>
    public static IReadOnlyList<HomeEntry> UnderMore { get; } = [HomeEntry.Syncs, HomeEntry.KnownHosts, HomeEntry.Overview];

    /// <summary>Der Eintrag, unter dem eine Seite steht.</summary>
    public static HomeEntry EntryOf(HomePage page) => page switch
    {
        HomePage.Snippets => HomeEntry.Snippets,
        HomePage.Keys or HomePage.Identities => HomeEntry.KeysAndIdentities,
        HomePage.Tunnels => HomeEntry.Tunnels,
        HomePage.Syncs => HomeEntry.Syncs,
        HomePage.KnownHosts => HomeEntry.KnownHosts,
        HomePage.Overview => HomeEntry.Overview,
        _ => HomeEntry.Servers,
    };

    /// <summary>
    /// Die Seite, die ein Klick auf den Eintrag zeigt; <c>null</c> für „Mehr“. „Schlüssel &amp; Identitäten“ bleibt bei
    /// der Hälfte, die schon offen ist, sonst die Schlüssel.
    /// </summary>
    public static HomePage? PageOf(HomeEntry entry, HomePage current) => entry switch
    {
        HomeEntry.Servers => HomePage.Servers,
        HomeEntry.Snippets => HomePage.Snippets,
        HomeEntry.KeysAndIdentities => current is HomePage.Keys or HomePage.Identities ? current : HomePage.Keys,
        HomeEntry.Tunnels => HomePage.Tunnels,
        HomeEntry.Syncs => HomePage.Syncs,
        HomeEntry.KnownHosts => HomePage.KnownHosts,
        HomeEntry.Overview => HomePage.Overview,
        _ => null,
    };

    /// <summary>Welcher Eintrag hervorgehoben ist: der der Seite – oder „Mehr“, wenn er darunter zugeklappt ist.</summary>
    public static HomeEntry Highlighted(HomePage page, bool moreOpen)
        => EntryOf(page) is var entry && !moreOpen && UnderMore.Contains(entry) ? HomeEntry.More : entry;
}
