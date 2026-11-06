using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

/// <summary>
/// Ein Verzeichnis im Baum neben der Dateiliste. Seine Unterverzeichnisse lädt der Browser erst, wenn es aufgeklappt
/// wird – bis dahin zeigt der Baum einen Pfeil, auch wenn es vielleicht keine gibt.
/// </summary>
public sealed partial class DirectoryNode : ObservableObject
{
    // Alle Unterverzeichnisse, die der Server genannt hat – auch die versteckten, die gerade nicht zu sehen sind.
    private IReadOnlyList<string> _names = [];

    internal DirectoryNode(string name, string path)
    {
        Name = name;
        Path = path;
    }

    public string Name { get; }

    public string Path { get; }

    /// <summary>Ohne Steuerzeichen und ohne Zeichen, die die Schreibrichtung umkehren – der Name kommt vom Server.</summary>
    public string DisplayName => Path == RemotePath.Root ? RemotePath.Root : Format.Visible(Name);

    public ObservableCollection<DirectoryNode> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Noch nicht geladen: Der Baum zeigt einen Pfeil, und Aufklappen lädt.</summary>
    [ObservableProperty]
    public partial bool HasUnrealizedChildren { get; private set; } = true;

    internal bool IsLoaded { get; private set; }

    /// <summary>Der Name – der Baum liest ihn Screenreadern vor.</summary>
    public override string ToString() => DisplayName;

    /// <summary>
    /// Übernimmt die Unterverzeichnisse, wie der Server sie nennt. Knoten, die es schon gibt, bleiben samt ihrem
    /// Zustand (aufgeklappt, geladen) erhalten.
    /// </summary>
    internal void Fill(IEnumerable<string> names, bool showHidden)
    {
        _names = [.. names];
        IsLoaded = true;
        HasUnrealizedChildren = false;
        Refilter(showHidden);
    }

    /// <summary>Zeigt die versteckten Unterverzeichnisse oder nicht – ohne neu zu laden.</summary>
    internal void Refilter(bool showHidden)
    {
        if (!IsLoaded)
        {
            return;
        }

        var wanted = _names.Where(name => showHidden || !name.StartsWith('.')).ToList();
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Children[i].Name))
            {
                Children.RemoveAt(i);
            }
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var existing = Children.FirstOrDefault(child => child.Name == wanted[i]);
            if (existing is null)
            {
                Children.Insert(i, new DirectoryNode(wanted[i], RemotePath.Combine(Path, wanted[i])));
            }
            else if (Children.IndexOf(existing) != i)
            {
                Children.Move(Children.IndexOf(existing), i);
            }

            Children[i].Refilter(showHidden);
        }
    }

    /// <summary>
    /// Ein Unterverzeichnis, das es geben muss, auch wenn die Liste es nicht nennt – der Weg zum angezeigten
    /// Verzeichnis führt hindurch (es ist versteckt, oder dieses hier ließ sich nicht auflisten).
    /// </summary>
    internal DirectoryNode Reach(string name)
    {
        if (Children.FirstOrDefault(child => child.Name == name) is { } child)
        {
            return child;
        }

        child = new DirectoryNode(name, RemotePath.Combine(Path, name));
        Children.Add(child);
        HasUnrealizedChildren = false;
        return child;
    }

    /// <summary>Ließ sich nicht auflisten: kein Pfeil mehr, der ins Leere führt.</summary>
    internal void MarkUnlistable()
    {
        IsLoaded = true;
        HasUnrealizedChildren = false;
    }
}

/// <summary>Der Baum der Verzeichnisse neben der Liste – derselbe Server, dieselbe Navigation.</summary>
public sealed partial class SftpBrowserViewModel
{
    /// <summary>Die Wurzel des Baums – genau ein Knoten, <c>/</c>.</summary>
    public ObservableCollection<DirectoryNode> TreeRoots { get; } = [];

    /// <summary>
    /// Ob der Baum zu sehen ist. Nur dann lädt der Browser für ihn – ausgeblendet kostet er keine Anfrage.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowTree { get; set; }

    /// <summary>Der Knoten des angezeigten Verzeichnisses; die Oberfläche markiert ihn im Baum.</summary>
    [ObservableProperty]
    public partial DirectoryNode? CurrentNode { get; private set; }

    /// <summary>Lädt die Unterverzeichnisse eines Knotens, der aufgeklappt wird – nur beim ersten Mal.</summary>
    public async Task ExpandAsync(DirectoryNode node)
    {
        if (node.IsLoaded || _files is not { } files)
        {
            return;
        }

        try
        {
            await LoadNodeAsync(files, node);
        }
        catch (RemoteFileException e)
        {
            node.MarkUnlistable();
            Error = Describe(e);
        }
    }

    /// <summary>Wechselt in das Verzeichnis eines Knotens.</summary>
    public Task OpenNodeAsync(DirectoryNode node) => NavigateToAsync(node.Path);

    private void ResetTree()
    {
        TreeRoots.Clear();
        TreeRoots.Add(new DirectoryNode(string.Empty, RemotePath.Root));
        CurrentNode = null;
    }

    // Die Liste hat ein Verzeichnis geladen: Was sie über seine Unterverzeichnisse weiß, gilt auch im Baum – so
    // stimmt er nach „Neuer Ordner“, Umbenennen und Löschen ohne eigene Anfrage.
    private async Task SyncTreeAsync(string path, IReadOnlyList<RemoteEntry> entries)
    {
        // Erst den Weg aufklappen – den Knoten des Verzeichnisses gibt es danach sicher.
        if (ShowTree)
        {
            await RevealAsync(path);
        }

        if (CurrentPath == path && Find(path) is { } node)
        {
            node.Fill(DirectoryNames(entries), ShowHidden);
        }
    }

    // Klappt den Baum bis zum Verzeichnis auf und markiert es. Ein Verzeichnis auf dem Weg, das sich nicht auflisten
    // lässt (keine Rechte auf /home), bekommt nur den einen Knoten, der weiterführt.
    private async Task RevealAsync(string path)
    {
        if (_files is not { } files || TreeRoots.Count == 0)
        {
            return;
        }

        // Die Wurzel ist immer offen – ein einzelner zugeklappter Knoten sähe aus wie ein leerer Baum.
        var node = TreeRoots[0];
        node.IsExpanded = true;
        foreach (var segment in RemotePath.Segments(path).Skip(1))
        {
            if (!node.IsLoaded)
            {
                try
                {
                    await LoadNodeAsync(files, node);
                }
                catch (RemoteFileException)
                {
                    node.MarkUnlistable();
                }

                // Unterdessen eine andere Verbindung oder ein anderes Verzeichnis: Dieser Weg gilt nicht mehr.
                if (!ReferenceEquals(files, _files) || CurrentPath != path)
                {
                    return;
                }
            }

            node.IsExpanded = true;
            node = node.Reach(segment.Name);
        }

        CurrentNode = node;
    }

    private async Task LoadNodeAsync(IRemoteFileSystem files, DirectoryNode node)
    {
        var entries = await ListAsync(files, node.Path);
        if (ReferenceEquals(files, _files))
        {
            node.Fill(DirectoryNames(entries), ShowHidden);
        }
    }

    /// <summary>
    /// Die wievielte Zeile ein Knoten im Baum ist, von oben gezählt und ab 0 – über alles, was aufgeklappt ist.
    /// Damit rollt die Ansicht zu einem Knoten, den sie unter sehr vielen Geschwistern noch nicht gezeichnet hat.
    /// <c>null</c>, wenn der Knoten nicht zu sehen wäre (er gehört nicht zum Baum, oder etwas darüber ist zu).
    /// </summary>
    public int? TreeRowOf(DirectoryNode node)
    {
        var row = 0;
        return Find(TreeRoots) ? row : null;

        bool Find(IEnumerable<DirectoryNode> nodes)
        {
            foreach (var candidate in nodes)
            {
                if (ReferenceEquals(candidate, node))
                {
                    return true;
                }

                row++;
                if (candidate.IsExpanded && Find(candidate.Children))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Die Verzeichnisse über einem Knoten, von der Wurzel bis zu seinem Elternknoten – der Weg, den die Ansicht
    /// abgeht, um zu einem Knoten zu rollen, den sie noch nicht aufgebaut hat. Leer, wenn der Knoten nicht zu
    /// diesem Baum gehört.
    /// </summary>
    public IReadOnlyList<DirectoryNode> TreePathTo(DirectoryNode node)
    {
        var path = new List<DirectoryNode>();
        if (TreeRoots.Count == 0)
        {
            return path;
        }

        var current = TreeRoots[0];
        foreach (var segment in RemotePath.Segments(node.Path).Skip(1))
        {
            path.Add(current);
            if (current.Children.FirstOrDefault(child => child.Name == segment.Name) is not { } next)
            {
                return [];
            }

            current = next;
        }

        return ReferenceEquals(current, node) ? path : [];
    }

    private DirectoryNode? Find(string path)
    {
        if (TreeRoots.Count == 0)
        {
            return null;
        }

        var node = TreeRoots[0];
        foreach (var segment in RemotePath.Segments(path).Skip(1))
        {
            node = node.Children.FirstOrDefault(child => child.Name == segment.Name);
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    private void RefilterTree()
    {
        foreach (var root in TreeRoots)
        {
            root.Refilter(ShowHidden);
        }

        // Liegt das angezeigte Verzeichnis selbst in einem versteckten, braucht der Baum den Weg dorthin weiter.
        if (ShowTree && IsAvailable)
        {
            _ = RevealAsync(CurrentPath);
        }
    }

    partial void OnShowTreeChanged(bool value)
    {
        if (value && IsAvailable && Segments.Count > 0)
        {
            _ = RevealAsync(CurrentPath);
        }
    }

    private static IEnumerable<string> DirectoryNames(IReadOnlyList<RemoteEntry> entries)
        => entries.Where(entry => entry.IsDirectory).Select(entry => entry.Name);
}
