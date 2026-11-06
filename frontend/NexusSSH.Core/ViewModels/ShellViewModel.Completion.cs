using NexusSSH.Core.Services;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.ViewModels;

// Ergänzen beim Tippen im Terminal: Die App gibt der Terminal-Seite Listen von Kandidaten, die Seite vergleicht sie
// mit dem Getippten. Nie umgekehrt – was getippt wird, verlässt die Seite nicht.
public sealed partial class ShellViewModel
{
    /// <summary>
    /// So lange gilt eine gelesene Liste, wenn die Shell dasselbe Verzeichnis wieder meldet (das tut sie mit jeder
    /// Eingabezeile): Neue Dateien tauchen danach auf, ohne dass der Server bei jeder Zeile gefragt wird.
    /// </summary>
    public static readonly TimeSpan PathNamesAge = TimeSpan.FromSeconds(15);

    /// <summary>So lange gilt der gelesene Inhalt eines Unterverzeichnisses – der wird seltener gebraucht und kostet je Verzeichnis eine Anfrage.</summary>
    public static readonly TimeSpan ChildNamesAge = TimeSpan.FromMinutes(2);

    /// <summary>Das Terminal ergänzt Unterbefehle und Schalter bekannter Programme aus mitgelieferten Tabellen.</summary>
    public bool OptionSuggestions
    {
        get => Settings.OptionSuggestions;
        set
        {
            if (value != Settings.OptionSuggestions)
            {
                Settings.OptionSuggestions = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Das Terminal ergänzt Namen aus dem Verzeichnis der Shell. Nur dann liest die App dieses Verzeichnis dafür;
    /// abgeschaltet fragt sie den Server nichts und nimmt der Seite die Listen wieder weg.
    /// </summary>
    public bool PathSuggestions
    {
        get => Settings.PathSuggestions;
        set
        {
            if (value == Settings.PathSuggestions)
            {
                return;
            }

            Settings.PathSuggestions = value;
            OnPropertyChanged();
            foreach (var session in Sessions)
            {
                if (value)
                {
                    _ = LoadPathNamesAsync(session, session.ShellDirectory ?? session.StartDirectory);
                }
                else
                {
                    session.ForgetPathNames();
                }
            }
        }
    }

    /// <summary>
    /// Das Terminal ergänzt als erstes Wort einer Zeile die Programme des Servers. Nur dann fragt die App nach dem
    /// Verbinden einmal nach ihren Namen; abgeschaltet nimmt sie der Seite die Listen wieder weg.
    /// </summary>
    public bool CommandSuggestions
    {
        get => Settings.CommandSuggestions;
        set
        {
            if (value == Settings.CommandSuggestions)
            {
                return;
            }

            Settings.CommandSuggestions = value;
            OnPropertyChanged();
            foreach (var session in Sessions)
            {
                if (value)
                {
                    _ = LoadCommandNamesAsync(session);
                }
                else if (session.CommandNames.Count > 0)
                {
                    session.CommandNames = [];
                }
            }
        }
    }

    /// <summary>
    /// Das Terminal ergänzt Namen, die die App schon kennt: Dienste und Container des Servers (sobald ihre Seite
    /// einmal offen war) und die Adressen der gespeicherten Server hinter <c>ssh</c>. Fragt niemanden.
    /// </summary>
    public bool KnownNameSuggestions
    {
        get => Settings.KnownNameSuggestions;
        set
        {
            if (value != Settings.KnownNameSuggestions)
            {
                Settings.KnownNameSuggestions = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TerminalHosts));
            }
        }
    }

    /// <summary>Tab nimmt im Terminal auch eine Ergänzung an. Aus: Tab bleibt dort der Shell.</summary>
    public bool TabCompletes
    {
        get => Settings.TabCompletes;
        set
        {
            if (value != Settings.TabCompletes)
            {
                Settings.TabCompletes = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Die Adressen der gespeicherten Server, die das Terminal hinter <c>ssh</c>, <c>scp</c> … ergänzen darf – leer,
    /// wenn das abgeschaltet ist. Sie stehen dann in jedem Terminal zur Wahl, verlassen diesen Rechner aber nur,
    /// wenn der Benutzer eine annimmt.
    /// </summary>
    public IReadOnlyList<string> TerminalHosts => KnownNameSuggestions ? Completions.Hosts(_profiles) : [];

    // Die Verbindung steht ganz (oder ist zu Ende – dann gibt es keine Namen mehr): Die Shell beginnt dort, wo
    // auch der Dateibrowser beginnt, und was vom Server bekannt war, gilt für eine neue Verbindung nicht mehr.
    private void OnConnectedForCompletion(SessionViewModel session)
    {
        session.ForgetPathNames();
        session.CommandNames = [];
        session.StartDirectory = session.State == ConnectionState.Connected && session.Files.IsAvailable ? session.Files.CurrentPath : null;
        _ = LoadPathNamesAsync(session, session.StartDirectory);
        _ = LoadCommandNamesAsync(session);
    }

    /// <summary>
    /// Liest die Namen eines Verzeichnisses für das Terminal einer Sitzung – über die Dateiverbindung, ohne Befehl am
    /// Server, und nur, wenn das Ergänzen eingeschaltet ist und die Sitzung Terminal und Dateien hat. Dasselbe
    /// Verzeichnis wird nicht öfter gelesen als alle <see cref="PathNamesAge"/>. Scheitert es, gibt es keine Namen.
    /// Danach, für Pfade eine Ebene tiefer, der Inhalt einiger Unterverzeichnisse (<see cref="LoadChildrenAsync"/>).
    /// </summary>
    internal async Task LoadPathNamesAsync(SessionViewModel session, string? path)
    {
        if (!PathSuggestions || path is null || session.IsVirtual || session.IsFilesOnly
            || session.State != ConnectionState.Connected || !session.Files.IsAvailable)
        {
            return;
        }

        var now = Now();
        if (session.NamesPath == path && now - session.NamesRead < PathNamesAge)
        {
            return;
        }

        (session.NamesPath, session.NamesRead) = (path, now);
        var request = ++session.NamesRequest;
        var entries = await session.Files.NamesAsync(path);

        // Inzwischen kann die Shell weitergezogen, die Verbindung weg oder das Ergänzen abgeschaltet sein.
        if (!IsCurrent(session, request))
        {
            return;
        }

        var names = entries is null ? [] : Completions.Names(entries);
        session.PathNames = names;
        await LoadChildrenAsync(session, path, names, request);
    }

    // Eine Ebene tiefer: Die Seite kann der App nicht sagen, in welches Unterverzeichnis getippt wird – also bekommt
    // sie den Inhalt der ersten paar gleich mit. Begrenzt in Zahl und Größe, nicht für versteckte, und nicht öfter
    // als alle ChildNamesAge je Verzeichnis: Jedes kostet den Server eine Anfrage.
    private async Task LoadChildrenAsync(SessionViewModel session, string path, IReadOnlyList<PathName> names, int request)
    {
        var directories = names.Where(name => name.Dir && !name.Name.StartsWith('.')).Take(Completions.MaxChildDirectories).ToList();
        if (directories.Count == 0)
        {
            return;
        }

        if (session.ChildNames.Count > 256)
        {
            session.ChildNames.Clear();
        }

        Dictionary<string, IReadOnlyList<PathName>> found = [];
        foreach (var directory in directories)
        {
            var child = RemotePath.Normalize(path.TrimEnd('/') + "/" + directory.Name);
            if (!session.ChildNames.TryGetValue(child, out var known) || Now() - known.Read >= ChildNamesAge)
            {
                var entries = await session.Files.NamesAsync(child);
                if (!IsCurrent(session, request))
                {
                    return;
                }

                known = (Now(), entries is null || entries.Count > Completions.MaxChildren ? [] : Completions.Names(entries));
                session.ChildNames[child] = known;
            }

            if (known.Names.Count > 0)
            {
                found[directory.Name] = known.Names;
            }
        }

        if (found.Count > 0)
        {
            session.PathNames = [.. names.Select(name => name.Dir && found.TryGetValue(name.Name, out var children) ? name with { Children = children } : name)];
        }
    }

    private bool IsCurrent(SessionViewModel session, int request)
        => request == session.NamesRequest && PathSuggestions && session.State == ConnectionState.Connected;

    /// <summary>
    /// Fragt einmal je Verbindung nach den Programmen im Suchpfad des Servers – ein fester Befehl im Kern. Nur, wenn
    /// das Ergänzen eingeschaltet ist und die Sitzung eine Shell und Befehle hat (kein Telnet, kein FTP, kein Fenster
    /// von tmux). Führt der Server keine Befehle aus, gibt es keine Namen.
    /// </summary>
    internal async Task LoadCommandNamesAsync(SessionViewModel session)
    {
        if (!CommandSuggestions || session.State != ConnectionState.Connected || session.IsVirtual || session.IsFilesOnly
            || session.Profile.IsFtp || session.Profile.IsLocal || session.Inspector.Monitor is not { } monitor)
        {
            return;
        }

        try
        {
            var names = await monitor.CommandNamesAsync();
            if (CommandSuggestions && session.State == ConnectionState.Connected && ReferenceEquals(monitor, session.Inspector.Monitor))
            {
                session.CommandNames = names;
            }
        }
        catch (RemoteFileException)
        {
        }
    }
}
