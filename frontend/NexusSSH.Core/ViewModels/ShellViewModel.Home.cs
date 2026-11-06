using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

/// <summary>Die Seiten der Startseite, in der Reihenfolge ihrer Seitenleiste.</summary>
public enum HomePage
{
    Servers,

    /// <summary>Gemerkte Abgleiche: lokaler Ordner und Ordner am Server, mit einem Klick erneut abgeglichen.</summary>
    Syncs,
    Snippets,
    Identities,

    /// <summary>Die eigenen Schlüsseldateien: ansehen, Passphrase ändern, zwischen OpenSSH und PuTTY umwandeln.</summary>
    Keys,
    KnownHosts,

    /// <summary>Alle Portweiterleitungen aller Server, mit ihrem Zustand.</summary>
    Tunnels,

    /// <summary>Eine Zeile je offener Sitzung mit ihrer Auslastung – solange sie zu sehen ist, messen alle.</summary>
    Overview,
}

// Die Gruppen der Startseite. Eine Gruppe ist ein Pfad (GroupPath): „Kunde/Projekt“ liegt in „Kunde“. Die
// Startseite zeigt immer eine Ebene – die Gruppen direkt unter der geöffneten als Abschnitte, jede mit den
// Profilen, die direkt in ihr liegen; tiefer führt ein Klick auf ihren Namen. Reihenfolge und Eingeklapptes stehen
// in den Einstellungen dieses Rechners; das Profil kennt nur den Namen seiner Gruppe.
public sealed partial class ShellViewModel
{
    /// <summary>
    /// Welche Seite die Startseite zeigt – ihre Seitenleiste wählt zwischen den Servern und den Verwaltungen, die
    /// früher Dialoge oder Abschnitte der Einstellungen waren.
    /// </summary>
    [ObservableProperty]
    public partial HomePage HomePage { get; set; }

    /// <summary>Zeigt die Startseite mit dieser Seite – die Sitzungen laufen weiter (<see cref="ShowHome()"/>).</summary>
    public void ShowHome(HomePage page)
    {
        Logs = null;
        IsSettingsOpen = false;
        IsHelpOpen = false;
        HomePage = page;
        ShowHome();
    }

    // Die bekannten Server werden gelesen, wenn ihre Seite aufgeht – nicht bei jedem Start der App.
    /// <summary>
    /// Ob die Übersicht aller Sitzungen gerade zu sehen ist: die Startseite mit dieser Seite. Nur dann messen alle
    /// offenen Sitzungen – jede Messung ist eine Schleife auf ihrem Server.
    /// </summary>
    public bool IsOverviewShown => ActiveSession is null && HomePage == HomePage.Overview && !IsSettingsOpen && !IsHelpOpen;

    /// <summary>
    /// Ob „Mehr“ in der Seitenleiste aufgeklappt ist (<see cref="HomeSidebar"/>). Geht eine Seite darunter auf – auch
    /// über die Palette –, klappt es von selbst auf.
    /// </summary>
    [ObservableProperty]
    public partial bool IsHomeMoreOpen { get; set; }

    /// <summary>Ein Klick auf einen Eintrag der Seitenleiste: zeigt seine Seite, „Mehr“ klappt auf oder zu.</summary>
    public void ChooseHomeEntry(HomeEntry entry)
    {
        if (HomeSidebar.PageOf(entry, HomePage) is { } page)
        {
            HomePage = page;
        }
        else
        {
            IsHomeMoreOpen = !IsHomeMoreOpen;
        }
    }

    partial void OnHomePageChanged(HomePage value)
    {
        if (HomeSidebar.UnderMore.Contains(HomeSidebar.EntryOf(value)))
        {
            IsHomeMoreOpen = true;
        }

        UpdateMonitoring();
        if (value == HomePage.KnownHosts)
        {
            LoadKnownHosts();
        }

        if (value == HomePage.Keys)
        {
            LoadKeyFiles();
        }
    }

    /// <summary>Die Gruppe, die die Startseite gerade zeigt; leer = ganz oben.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInHomeFolder))]
    public partial string HomeFolder { get; private set; } = string.Empty;

    /// <summary>Der Weg zur geöffneten Gruppe, beginnend mit „Alle Server“.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HomeCrumb> HomeCrumbs { get; private set; } = BindableList.Of<HomeCrumb>([]);

    public bool IsInHomeFolder => HomeFolder.Length > 0;

    /// <summary>Alle Gruppen samt denen, die nur als Ordner anderer vorkommen – z. B. für „In Gruppe verschieben“.</summary>
    public IReadOnlyList<string> GroupPaths
    {
        get
        {
            var paths = new List<string>();
            void Add(string folder)
            {
                foreach (var child in Children(folder))
                {
                    paths.Add(child);
                    Add(child);
                }
            }

            Add(string.Empty);
            return paths;
        }
    }

    /// <summary>Öffnet eine Gruppe auf der Startseite; der leere Pfad führt nach ganz oben.</summary>
    public void OpenHomeFolder(string path)
    {
        HomeFolder = Existing(GroupPath.Normalize(path));
        UpdateHome();
    }

    /// <summary>Klappt einen Abschnitt der Startseite ein oder wieder aus.</summary>
    public void ToggleGroup(ProfileGroup group)
    {
        if (!Settings.CollapsedGroups.Remove(group.Key))
        {
            Settings.CollapsedGroups.Add(group.Key);
        }

        UpdateHome();
    }

    /// <summary>
    /// Die Kennfarbe, die für eine Gruppe gilt: ihre eigene, sonst die der nächsten Gruppe darüber; <c>null</c>,
    /// wenn keine von ihnen eine hat.
    /// </summary>
    public string? GroupColorOf(string group)
    {
        for (var path = GroupPath.Normalize(group); path.Length > 0; path = GroupPath.Parent(path))
        {
            if (Settings.GroupColors.TryGetValue(path, out var color) && ProfileColors.Find(color).Id.Length > 0)
            {
                return color;
            }
        }

        return null;
    }

    /// <summary>Die Farbe, die an dieser Gruppe selbst eingestellt ist (ohne die der Gruppen darüber).</summary>
    public string? OwnGroupColor(ProfileGroup group) => Settings.GroupColors.GetValueOrDefault(group.Path);

    /// <summary>
    /// Gibt einer Gruppe eine Kennfarbe – für alle Profile darin und darunter, die keine eigene haben. Ohne Farbe
    /// (leer oder unbekannt) fällt sie weg. Die Profile selbst ändern sich nicht.
    /// </summary>
    public void SetGroupColor(ProfileGroup group, string? color)
    {
        if (!group.CanMove || group.Path.Length == 0)
        {
            return;
        }

        if (ProfileColors.Find(color).Id is { Length: > 0 } id)
        {
            Settings.GroupColors[group.Path] = id;
        }
        else
        {
            Settings.GroupColors.Remove(group.Path);
        }

        LoadProfiles();
    }

    /// <summary>
    /// Das eigene Zeichen eines Servers, wie es gilt; <c>null</c>, wenn der Benutzer ihm keines gegeben hat. Das
    /// seiner Gruppe nennt <see cref="GroupMarkOf"/> – auf der Kachel steht zwischen beiden das Betriebssystem
    /// (<see cref="TileMarks.Face"/>).
    /// </summary>
    public string? MarkOf(ServerProfile profile) => TileMarks.Normalize(Settings.ServerMarks.GetValueOrDefault(profile.Id));

    /// <summary>
    /// Das Zeichen, das für eine Gruppe gilt: ihr eigenes, sonst das der nächsten Gruppe darüber; <c>null</c>, wenn
    /// keine von ihnen eines hat.
    /// </summary>
    public string? GroupMarkOf(string group)
    {
        for (var path = GroupPath.Normalize(group); path.Length > 0; path = GroupPath.Parent(path))
        {
            if (TileMarks.Normalize(Settings.GroupMarks.GetValueOrDefault(path)) is { } mark)
            {
                return mark;
            }
        }

        return null;
    }

    /// <summary>Das Zeichen, das an diesem Server selbst eingestellt ist (ohne das seiner Gruppe).</summary>
    public string? OwnMark(ServerProfile profile) => Settings.ServerMarks.GetValueOrDefault(profile.Id);

    /// <summary>Das Zeichen, das an dieser Gruppe selbst eingestellt ist.</summary>
    public string? OwnGroupMark(ProfileGroup group) => Settings.GroupMarks.GetValueOrDefault(group.Path);

    /// <summary>Gibt einem Server ein eigenes Zeichen; ohne (leer oder ungültig) gilt wieder das der Gruppe oder das Betriebssystem.</summary>
    public void SetMark(ServerProfile profile, string? mark)
    {
        SetMark(Settings.ServerMarks, profile.Id, mark);
        LoadProfiles();
    }

    /// <summary>Gibt einer Gruppe ein Zeichen – für alle Server darin und darunter, die kein eigenes haben.</summary>
    public void SetGroupMark(ProfileGroup group, string? mark)
    {
        if (group.CanMove && group.Path.Length > 0)
        {
            SetMark(Settings.GroupMarks, group.Path, mark);
            LoadProfiles();
        }
    }

    private static void SetMark(Dictionary<string, string> marks, string key, string? mark)
    {
        if (TileMarks.Normalize(mark) is { } valid)
        {
            marks[key] = valid;
        }
        else
        {
            marks.Remove(key);
        }
    }

    /// <summary>Ob sich die Gruppe unter ihren Nachbarn um <paramref name="delta"/> Plätze verschieben lässt.</summary>
    public bool CanMoveGroup(ProfileGroup group, int delta)
    {
        var siblings = Children(GroupPath.Parent(group.Path));
        var index = siblings.IndexOf(group.Path);
        return group.CanMove && index >= 0 && index + delta >= 0 && index + delta < siblings.Count;
    }

    /// <summary>Verschiebt eine Gruppe unter ihren Nachbarn (−1 = nach oben). Die Profile bleiben, wie sie sind.</summary>
    public void MoveGroup(ProfileGroup group, int delta)
    {
        if (!CanMoveGroup(group, delta))
        {
            return;
        }

        var siblings = Children(GroupPath.Parent(group.Path));
        var index = siblings.IndexOf(group.Path);
        siblings.RemoveAt(index);
        siblings.Insert(index + delta, group.Path);

        // Die Nachbarn stehen danach beisammen in der neuen Reihenfolge; andere Ebenen berührt das nicht.
        Settings.GroupOrder.RemoveAll(siblings.Contains);
        Settings.GroupOrder.AddRange(siblings);
        LoadProfiles();
    }

    /// <summary>Legt ein Profil in eine andere Gruppe (leer = ohne Gruppe). <c>false</c>, wenn es nicht gespeichert wurde.</summary>
    public bool MoveProfileToGroup(ServerProfile profile, string group)
    {
        var target = GroupPath.Normalize(group);
        return GroupPath.Normalize(profile.Group) == target || SaveProfile(profile with { Group = target }) is not null;
    }

    // Gruppen in der Reihenfolge des Benutzers, Untergruppen gleich hinter ihrer Gruppe; Profile ohne Gruppe zuletzt.
    private void LoadProfiles()
    {
        // Die Farbe der Gruppe steht nicht im Profil: Sie kommt beim Laden dazu, nur für die Anzeige.
        // Ebenso das eigene Zeichen des Servers und das seiner Gruppe – getrennt, denn das Betriebssystem steht dazwischen.
        _profiles = [.. _backend.Profiles.GetProfiles().Select(profile => profile with
        {
            GroupColor = GroupColorOf(profile.Group),
            Mark = MarkOf(profile),
            GroupMark = GroupMarkOf(profile.Group),
        })];
        OnPropertyChanged(nameof(TerminalHosts));
        var byGroup = _profiles.ToLookup(profile => GroupPath.Normalize(profile.Group));
        var groups = GroupPaths
            .Where(byGroup.Contains)
            .Select(path => new ProfileGroup(path, Ordered(byGroup[path])) { Path = path, Total = byGroup[path].Count(), Mark = GroupMarkOf(path), Color = GroupColorOf(path) })
            .ToList();
        if (byGroup.Contains(string.Empty))
        {
            groups.Add(new ProfileGroup(L.Get("Ungrouped"), Ordered(byGroup[string.Empty])) { Kind = GroupKind.Ungrouped, Total = byGroup[string.Empty].Count() });
        }

        ProfileGroups = BindableList.Of(groups);

        // Die Liste der Abgleiche nennt Server beim Namen, die Sprungliste auch.
        OnPropertyChanged(nameof(SavedSyncs));
        RecentMayHaveChanged();

        // Was es nicht mehr gibt, braucht keinen Platz in den Einstellungen – und die geöffnete Gruppe kann weg sein.
        var known = groups.SelectMany(group => GroupPath.WithAncestors(group.Path)).ToHashSet(StringComparer.Ordinal);
        Settings.GroupOrder.RemoveAll(path => !known.Contains(path));
        Settings.CollapsedGroups.RemoveAll(key => !key.StartsWith('*') && !known.Contains(key));
        foreach (var gone in Settings.GroupColors.Keys.Where(path => !known.Contains(path)).ToList())
        {
            Settings.GroupColors.Remove(gone);
        }

        foreach (var gone in Settings.GroupMarks.Keys.Where(path => !known.Contains(path)).ToList())
        {
            Settings.GroupMarks.Remove(gone);
        }

        foreach (var gone in Settings.ServerMarks.Keys.Where(id => _profiles.All(profile => profile.Id != id)).ToList())
        {
            Settings.ServerMarks.Remove(gone);
        }

        HomeFolder = Existing(HomeFolder);
        UpdateHome();
        RefreshTunnels();
    }

    // Die Gruppen direkt unter einem Ordner: erst die, die der Benutzer geordnet hat, dann die übrigen in der
    // Reihenfolge, in der sie unter den Profilen zuerst vorkommen.
    private List<string> Children(string folder)
    {
        var order = Settings.GroupOrder;
        return [.. _profiles
            .Select(profile => GroupPath.ChildOf(GroupPath.Normalize(profile.Group), folder))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Select((path, seen) => (path, seen, rank: order.IndexOf(path)))
            .OrderBy(child => child.rank < 0)
            .ThenBy(child => child.rank)
            .ThenBy(child => child.seen)
            .Select(child => child.path)];
    }

    // Die Gruppe selbst oder, wenn es sie nicht mehr gibt, die nächste darüber, die es noch gibt.
    private string Existing(string folder)
    {
        while (folder.Length > 0 && !_profiles.Any(profile => GroupPath.IsWithin(GroupPath.Normalize(profile.Group), folder)))
        {
            folder = GroupPath.Parent(folder);
        }

        return folder;
    }

    // Die Abschnitte der Startseite. Mit Filter: alles, was passt, nach Gruppen – über alle Ebenen, nichts ist
    // eingeklappt. Ohne Filter: die geöffnete Ebene. Jedes Wort des Filters muss irgendwo am Profil vorkommen.
    private void UpdateHome()
    {
        var words = HomeFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sections = words.Length > 0 ? Found(words) : Level(HomeFolder);
        HomeGroups = BindableList.Of(sections);

        var crumbs = new List<HomeCrumb> { new(L.Get("AllServers"), string.Empty) };
        crumbs.AddRange(GroupPath.WithAncestors(HomeFolder).Select(path => new HomeCrumb(GroupPath.Name(path), path)));
        HomeCrumbs = BindableList.Of(crumbs);
        HomeTableChanged();
    }

    private List<ProfileGroup> Found(string[] words)
    {
        bool Shown(ServerProfile profile) => words.All(word => Matches(profile, word));

        var sections = new List<ProfileGroup>();
        var favorites = ProfileGroups.SelectMany(group => group).Where(profile => profile.IsFavorite && Shown(profile)).ToList();
        if (favorites.Count > 0)
        {
            sections.Add(new ProfileGroup(L.Get("Favorites"), favorites) { Kind = GroupKind.Favorites, Total = favorites.Count });
        }

        foreach (var group in ProfileGroups)
        {
            var others = group.Where(profile => !profile.IsFavorite && Shown(profile)).ToList();
            if (others.Count > 0)
            {
                sections.Add(new ProfileGroup(group.Name, others) { Path = group.Path, Kind = group.Kind, Total = others.Count, Mark = group.Mark, Color = group.Color });
            }
        }

        return sections;
    }

    private List<ProfileGroup> Level(string folder)
    {
        var sections = new List<ProfileGroup>();
        var top = folder.Length == 0;
        var direct = ProfileGroups.ToDictionary(group => group.Kind == GroupKind.Ungrouped ? string.Empty : group.Path, group => group.Profiles);
        IReadOnlyList<ServerProfile> In(string path) => direct.TryGetValue(path, out var profiles) ? profiles : [];

        void Add(string name, GroupKind kind, string path, IReadOnlyList<ServerProfile> profiles, int total, int subgroups)
        {
            var section = new ProfileGroup(name, []) { Kind = kind, Path = path, Total = total, Subgroups = subgroups };
            var collapsed = Settings.CollapsedGroups.Contains(section.Key);

            // Zeichen und Farbe tragen nur echte Gruppen; „Favoriten“ und „Ohne Gruppe“ haben keinen Pfad.
            var real = path.Length > 0;
            sections.Add(new ProfileGroup(name, collapsed ? [] : profiles)
            {
                Kind = kind,
                Path = path,
                Total = total,
                Subgroups = subgroups,
                IsCollapsed = collapsed,
                Mark = real ? GroupMarkOf(path) : null,
                Color = real ? GroupColorOf(path) : null,
            });
        }

        if (top)
        {
            // Ganz oben stehen die Favoriten aller Gruppen als eigener Abschnitt – und dafür nicht noch einmal in ihrer.
            var favorites = ProfileGroups.SelectMany(group => group).Where(profile => profile.IsFavorite).ToList();
            if (favorites.Count > 0)
            {
                Add(L.Get("Favorites"), GroupKind.Favorites, string.Empty, favorites, favorites.Count, 0);
            }
        }
        else if (In(folder) is { Count: > 0 } here)
        {
            Add(GroupPath.Name(folder), GroupKind.Here, folder, here, here.Count, 0);
        }

        foreach (var child in Children(folder))
        {
            var shown = top ? [.. In(child).Where(profile => !profile.IsFavorite)] : In(child);
            var total = _profiles.Count(profile => GroupPath.IsWithin(GroupPath.Normalize(profile.Group), child));
            var subgroups = Children(child).Count;
            if (shown.Count > 0 || subgroups > 0)
            {
                Add(GroupPath.Name(child), GroupKind.Group, child, shown, total, subgroups);
            }
        }

        if (top && In(string.Empty).Where(profile => !profile.IsFavorite).ToList() is { Count: > 0 } loose)
        {
            Add(L.Get("Ungrouped"), GroupKind.Ungrouped, string.Empty, loose, loose.Count, 0);
        }

        return sections;
    }
}
