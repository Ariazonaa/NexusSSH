using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.Terminals.Tmux;

namespace NexusSSH.Core.ViewModels;

// Der Steuermodus von tmux (tmux -CC): Die Fenster einer tmux-Sitzung stehen als Tabs der App, ihre Bereiche als
// geteiltes Terminal. Ein Tab – der Träger – hält die Verbindung und die Shell, in der tmux im Steuermodus läuft;
// die Tabs der Fenster hängen an ihm. Reißt die Verbindung, läuft die Sitzung auf dem Server weiter: neu
// verbinden, wieder anhängen, alles ist noch da.
public sealed partial class ShellViewModel
{
    // Ein Fenster von tmux in der App: ein Tab je gezeigtem Bereich. Lässt sich die Anordnung nicht nachbilden,
    // ist es ein einziger Tab, der dem aktiven Bereich folgt.
    private sealed class TmuxWindow
    {
        public List<(SessionViewModel Tab, TmuxPane Pane)> Members { get; } = [];

        // Der Titel, den die Reiter zuletzt bekamen.
        public string Title { get; set; } = string.Empty;

        // Die Größen, die tmux zuletzt für dieses Fenster genannt bekam – dieselben schickt die App nicht noch einmal.
        public string Sized { get; set; } = string.Empty;

        // Wie groß die Bereiche bei tmux sind, soweit die App es weiß: was tmux zuletzt nannte oder, wenn danach noch
        // Größen hinausgingen, was es genannt bekam. Ein Bereich, der fehlt, gilt als unbekannt.
        public Dictionary<string, (int Columns, int Rows)> AtTmux { get; set; } = [];

        // Die Terminals melden ihre Größen eines nach dem anderen; erst wenn eine Weile keines mehr gemeldet hat,
        // bekommt tmux sie (SizeTmuxWindow). Jede Meldung zieht eine neue Nummer, nur die letzte gilt.
        public int SizingTicket { get; set; }
    }

    private sealed class TmuxLink(SessionViewModel carrier, string name)
    {
        public SessionViewModel Carrier { get; } = carrier;

        public string Name { get; } = name;

        public TmuxStream Stream { get; } = new();

        public TmuxClient? Client { get; set; }

        // Fenster → seine Tabs; nur im Thread der Oberfläche angefasst.
        public Dictionary<string, TmuxWindow> Windows { get; } = [];

        // Bereich → Verbindung; die Ausgabe kommt aus einem Thread des Backends.
        public ConcurrentDictionary<string, TmuxPane> Panes { get; } = new();

        public IEnumerable<SessionViewModel> Tabs => Windows.Values.SelectMany(window => window.Members).Select(member => member.Tab);

        public bool Started { get; set; }

        public bool Refreshing { get; set; }

        public bool RefreshAgain { get; set; }

        public bool Ended { get; set; }

        // Der Benutzer hat ein neues Fenster verlangt: Sein Tab kommt nach vorn.
        public bool FollowNew { get; set; }

        // Wie oft die App tmux schon Größen genannt hat. Eine Liste der Bereiche gibt die Größen bei tmux nur dann
        // richtig wieder, wenn danach keine mehr hinausgingen – die Antworten kommen in der Reihenfolge der Befehle.
        public int SizingRounds;
    }

    /// <summary>So lange müssen die Terminals eines tmux-Fensters still sein, bevor tmux ihre Größen bekommt.</summary>
    public static readonly TimeSpan TmuxSizingPause = TimeSpan.FromMilliseconds(100);

    private readonly List<TmuxLink> _tmux = [];

    /// <summary>So lange hat tmux Zeit, den Steuermodus zu beginnen, nachdem der Befehl getippt ist.</summary>
    public static readonly TimeSpan TmuxPatience = TimeSpan.FromSeconds(10);

    /// <summary>Ob der Tab gerade eine tmux-Sitzung im Steuermodus trägt.</summary>
    public bool CarriesTmux(SessionViewModel session) => _tmux.Any(link => ReferenceEquals(link.Carrier, session));

    /// <summary>
    /// Hängt im Steuermodus an eine tmux-Sitzung: tippt <c>tmux -CC attach-session</c> in die Shell des Tabs und
    /// zeigt die Fenster von tmux danach als eigene Tabs. Nur für Sitzungen mit schlichtem Namen
    /// (<see cref="MuxSession.IsSafe"/>) – der Name wird getippt.
    /// </summary>
    public bool AttachTmuxControl(SessionViewModel carrier, MuxSession mux)
    {
        if (mux is not { Kind: "tmux", IsSafe: true }
            || carrier is not { State: ConnectionState.Connected, IsFilesOnly: false, IsVirtual: false }
            || CarriesTmux(carrier))
        {
            return false;
        }

        var link = new TmuxLink(carrier, mux.Name);
        var client = new TmuxClient(carrier.WriteRaw);
        link.Client = client;
        _tmux.Add(link);

        client.Started += () => _dispatcher.Post(() =>
        {
            link.Started = true;
            Log(L.Get("TmuxStarted", Format.Visible(carrier.Profile.Name), link.Name));
            _ = BeginTmuxAsync(link, client);
        });
        client.WindowsChanged += () => _dispatcher.Post(() => _ = RefreshTmuxAsync(link));
        client.Output += (pane, data) =>
        {
            if (link.Panes.TryGetValue(pane, out var target))
            {
                target.Receive(data);
            }
        };
        client.Paused += pane =>
        {
            if (link.Panes.TryGetValue(pane, out var target))
            {
                target.PausedByTmux();
            }
        };
        client.Exited += reason => _dispatcher.Post(() => EndTmux(link, reason));

        // Ab jetzt geht die Ausgabe des Trägers erst durch den Leser: Zeilen des Steuermodus sind kein Text für den
        // Bildschirm. Und was im Träger getippt wird, wäre für tmux ein Befehl – also geht dort nichts hinein.
        carrier.OutputTap = (data, forward) => link.Stream.Feed(
            data.Span,
            plain => forward(plain),
            client.OnLine,
            () => { });
        carrier.InputGate = () => !link.Stream.InControl;
        carrier.ConnectionEnded += OnTmuxCarrierEnded;

        carrier.WriteRaw(Encoding.ASCII.GetBytes($"tmux -CC attach-session -t ={mux.Name}\r"));

        // Kommt der Steuermodus nicht zustande (kein tmux -CC, Sitzung inzwischen weg), bleibt der Tab ein
        // gewöhnliches Terminal.
        _ = Task.Delay(TmuxPatience).ContinueWith(
            _ => _dispatcher.Post(() =>
            {
                if (!link.Started && !link.Ended)
                {
                    // tmux ist im Steuermodus, spricht ihn aber anders, als die App ihn versteht (sehr altes
                    // tmux): abhängen, bevor der Tab wieder ein Terminal wird – sonst wäre dort jede getippte
                    // Zeile ein Befehl an tmux.
                    if (link.Stream.InControl)
                    {
                        carrier.WriteRaw("detach-client\n"u8.ToArray());
                    }

                    EndTmux(link, null, started: false);
                }
            }),
            TaskScheduler.Default);
        return true;
    }

    // Erst die Version (danach richtet sich, wie getippt wird), dann die Fenster.
    private async Task BeginTmuxAsync(TmuxLink link, TmuxClient client)
    {
        await client.ProbeAsync();
        await RefreshTmuxAsync(link);
    }

    private TmuxLink? TmuxOf(SessionViewModel? session)
        => session is null
            ? null
            : _tmux.FirstOrDefault(link => ReferenceEquals(link.Carrier, session) || link.Tabs.Any(tab => ReferenceEquals(tab, session)));

    /// <summary>Ob der aktive Tab zu einer tmux-Sitzung im Steuermodus gehört (ihr Träger oder eines ihrer Fenster).</summary>
    public bool IsInTmux => TmuxOf(ActiveSession) is { Started: true };

    /// <summary>Öffnet in der tmux-Sitzung des aktiven Tabs ein neues Fenster – sein Tab erscheint, sobald tmux es meldet.</summary>
    public void NewTmuxWindow()
    {
        if (TmuxOf(ActiveSession) is { Started: true, Client: { } client } link)
        {
            link.FollowNew = true;
            client.NewWindow();
        }
    }

    /// <summary>
    /// Gibt einem Tab einen Namen. Der Tab eines tmux-Fensters benennt das Fenster bei tmux um (der Reiter folgt,
    /// sobald tmux es meldet) – aber nur mit einem schlichten Namen, denn er wird Teil eines Befehls.
    /// </summary>
    public void RenameTab(SessionViewModel session, string? name)
    {
        foreach (var link in _tmux)
        {
            foreach (var (id, window) in link.Windows)
            {
                if (!window.Members.Exists(member => ReferenceEquals(member.Tab, session)))
                {
                    continue;
                }

                if (link.Client?.RenameWindow(id, (name ?? string.Empty).Trim()) != true)
                {
                    Log(L.Get("TmuxNameRefused"), LogSeverity.Warning);
                }

                return;
            }
        }

        session.Rename(name);
    }

    private void OnTmuxCarrierEnded(SessionViewModel carrier, ConnectionStatus status)
    {
        if (_tmux.FirstOrDefault(link => ReferenceEquals(link.Carrier, carrier)) is { } link)
        {
            EndTmux(link, null);
        }
    }

    // Liest die Fenster neu und gleicht die Tabs damit ab. Meldungen kommen in Schüben – es läuft immer nur ein
    // Abgleich, und ein weiterer folgt, wenn unterdessen wieder etwas kam.
    private async Task RefreshTmuxAsync(TmuxLink link)
    {
        if (link.Ended || link.Client is not { } client)
        {
            return;
        }

        if (link.Refreshing)
        {
            link.RefreshAgain = true;
            return;
        }

        link.Refreshing = true;
        try
        {
            do
            {
                link.RefreshAgain = false;
                var rounds = Volatile.Read(ref link.SizingRounds);
                IReadOnlyList<TmuxWindowInfo> windows;
                try
                {
                    windows = await client.WindowsAsync();
                }
                catch (TmuxException)
                {
                    return;
                }

                var apply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _dispatcher.Post(() =>
                {
                    try
                    {
                        ApplyTmuxWindows(link, client, windows, current: Volatile.Read(ref link.SizingRounds) == rounds);
                    }
                    finally
                    {
                        apply.TrySetResult();
                    }
                });
                await apply.Task;
            }
            while (link.RefreshAgain && !link.Ended);
        }
        finally
        {
            link.Refreshing = false;
        }
    }

    // „current“: Seit die Liste erbeten wurde, ging keine Größe an tmux – sie nennt die Größen, die dort gelten.
    private void ApplyTmuxWindows(TmuxLink link, TmuxClient client, IReadOnlyList<TmuxWindowInfo> windows, bool current)
    {
        if (link.Ended || !Sessions.Contains(link.Carrier))
        {
            return;
        }

        const int MostWindows = 40;
        var first = link.Windows.Count == 0;
        windows = [.. windows.Where(window => window.Panes.Count > 0).Take(MostWindows)];

        // Was es nicht mehr gibt, verliert seine Tabs.
        foreach (var gone in link.Windows.Keys.Except(windows.Select(window => window.Id)).ToList())
        {
            foreach (var member in link.Windows[gone].Members.ToList())
            {
                DropTmuxMember(link, link.Windows[gone], member);
            }

            link.Windows.Remove(gone);
        }

        SessionViewModel? added = null;
        foreach (var window in windows)
        {
            var isNew = !link.Windows.TryGetValue(window.Id, out var known);
            known ??= link.Windows[window.Id] = new TmuxWindow();
            if (current)
            {
                known.AtTmux = window.Panes.ToDictionary(pane => pane.Id, pane => (pane.Width, pane.Height));
            }

            // Alle Bereiche als Baum, in der Reihenfolge der Fläche (oben vor unten, links vor rechts) – sonst, bei
            // einem einzelnen oder mehr als sechzehn, der aktive allein.
            var shape = TmuxTree.Of(window.Panes) as SplitBranch<TmuxPaneInfo>;
            IReadOnlyList<TmuxPaneInfo> wanted = shape is null
                ? [window.ActivePane!]
                : [.. window.Panes.OrderBy(pane => pane.Top).ThenBy(pane => pane.Left)];

            if (wanted.Count == 1 && known.Members is [var only])
            {
                // Ein Tab für das Fenster: Er folgt dem aktiven Bereich, statt neu zu entstehen.
                if (only.Pane.Pane != wanted[0].Id)
                {
                    link.Panes.TryRemove(only.Pane.Pane, out _);
                    link.Panes[wanted[0].Id] = only.Pane;
                    only.Pane.Show(wanted[0].Id);
                    known.Sized = string.Empty;
                }
            }
            else
            {
                foreach (var member in known.Members.Where(member => !wanted.Any(pane => pane.Id == member.Pane.Pane)).ToList())
                {
                    DropTmuxMember(link, known, member);
                    known.Sized = string.Empty;
                }

                for (var position = 0; position < wanted.Count; position++)
                {
                    var pane = wanted[position];
                    if (known.Members.FindIndex(member => member.Pane.Pane == pane.Id) is >= 0 and var at)
                    {
                        // In der Reihenfolge von tmux: links vor rechts, oben vor unten.
                        if (at != position)
                        {
                            var moved = known.Members[at];
                            known.Members.RemoveAt(at);
                            known.Members.Insert(Math.Min(position, known.Members.Count), moved);
                        }

                        continue;
                    }

                    var connection = new TmuxPane(client, window.Id, pane.Id, link.Carrier.Connection, _ => QueueTmuxSizing(link, window.Id));
                    link.Panes[pane.Id] = connection;

                    // Hinter dem Träger und allem davor, damit die Reiter zusammenstehen.
                    var anchor = known.Members.Count > 0 ? known.Members[^1].Tab : link.Tabs.LastOrDefault() ?? link.Carrier;
                    var tab = AddSession(link.Carrier.Profile, Math.Min(Sessions.IndexOf(anchor) + 1, Sessions.Count), activate: false, connect: () => connection);
                    known.Members.Insert(Math.Min(position, known.Members.Count), (tab, connection));
                    known.Title = string.Empty;
                    known.Sized = string.Empty;
                    added ??= isNew ? tab : null;
                }
            }

            // Der Name eines Fensters kommt vom Server: als reiner Text an die Reiter.
            var title = $"{link.Carrier.Profile.Name} · {Format.Visible(window.Name is { Length: > 0 } ? window.Name : window.Id)}";
            if (known.Title != title)
            {
                known.Title = title;
                foreach (var member in known.Members)
                {
                    member.Tab.Rename(title);
                }
            }

            ArrangeTmuxWindow(known, shape);
        }

        UpdateLayout();

        // Beim ersten Mal: Der Benutzer wollte zu tmux – also das erste Fenster nach vorn. Später folgt die App
        // nur einem Fenster, das er selbst verlangt hat.
        if (first && windows.Count > 0 && ReferenceEquals(ActiveSession, link.Carrier))
        {
            ActiveSession = link.Windows[windows[0].Id].Members[0].Tab;
        }
        else if (link.FollowNew && added is not null && !first)
        {
            link.FollowNew = false;
            ActiveSession = added;
        }
    }

    // Die Tabs eines Fensters stehen als eine Teilung beisammen, als Baum wie bei tmux. Die Anordnung gibt tmux vor
    // (Fixed); die Anteile am Platz gehören der App, sobald die Teilung steht: Sie bleiben, solange tmux dieselbe
    // Anordnung meldet – tmux richtet sich nach den Größen, die die App ihm nennt (SizeTmuxWindow), nicht
    // umgekehrt.
    private void ArrangeTmuxWindow(TmuxWindow window, SplitNode<TmuxPaneInfo>? shape)
    {
        var tabs = window.Members.Select(member => member.Tab).ToList();
        if (tabs.Count < 2 || shape is null)
        {
            return;
        }

        var byPane = window.Members.ToDictionary(member => member.Pane.Pane, member => member.Tab);
        if (!SplitTree.Leaves(shape).All(pane => byPane.ContainsKey(pane.Id)))
        {
            return;
        }

        var tree = SplitTree.Map(shape, pane => byPane[pane.Id]);
        var group = SplitOf(tabs[0]);
        if (group is { Fixed: true } && group.Members.Count == tabs.Count && !group.Members.Except(tabs).Any()
            && TmuxShapeOf(group.Root, tabs) == TmuxShapeOf(tree, tabs))
        {
            return;
        }

        foreach (var tab in tabs)
        {
            RemoveFromSplit(tab);
        }

        window.Sized = string.Empty;
        _splits.Add(new SplitGroup(tree, fixedByTmux: true));
        foreach (var tab in tabs)
        {
            tab.IsSplit = true;
        }
    }

    // Wie ein Baum gebaut ist, ohne die Anteile: Richtung und Reihenfolge der Tabs.
    private static string TmuxShapeOf(SplitNode<SessionViewModel> node, List<SessionViewModel> tabs) => node switch
    {
        SplitLeaf<SessionViewModel> leaf => tabs.IndexOf(leaf.Item).ToString(CultureInfo.InvariantCulture),
        SplitBranch<SessionViewModel> branch => $"{(branch.SideBySide ? 'H' : 'V')}({string.Join(',', branch.Children.Select(child => TmuxShapeOf(child, tabs)))})",
        _ => string.Empty,
    };

    // Ein Terminal des Fensters hat eine neue Größe gemeldet. Bei einem Umbau melden alle nacheinander, beim Ziehen
    // einer Linie in jedem Bild – tmux bekommt die Größen erst, wenn eine Weile keines mehr meldet: Vorher stünden
    // alte und neue nebeneinander, und tmux bekäme Zwischengrößen, die es nie gab.
    private void QueueTmuxSizing(TmuxLink link, string id)
    {
        if (link.Ended || !link.Windows.TryGetValue(id, out var window))
        {
            return;
        }

        var ticket = ++window.SizingTicket;
        _ = Task.Delay(TmuxSizingPause).ContinueWith(
            _ => _dispatcher.Post(() =>
            {
                if (window.SizingTicket == ticket)
                {
                    SizeTmuxWindow(link, id);
                }
            }),
            TaskScheduler.Default);
    }

    // Die Terminals eines Fensters haben ihre Größe in der App; tmux bekommt sie so genannt, dass seine Bereiche
    // genauso groß sind. Der Client ist so groß wie der Baum: nebeneinander die Summe der Breiten samt Trennlinien
    // und die größte Höhe, untereinander umgekehrt. Dann bekommt jedes Kind eines Zweigs außer dem letzten sein Maß
    // (`resize-pane`, von oben nach unten); das letzte nimmt den Rest. Verschoben wird dabei nichts: Wo ein Bereich
    // liegt, bestimmt weiter tmux – nie `select-layout`. Genannt wird nur, was bei tmux nicht schon so ist
    // (ResizeTmuxBranches) – jede Größe einmal.
    private void SizeTmuxWindow(TmuxLink link, string id)
    {
        if (link.Ended || link.Client is not { } client || !link.Windows.TryGetValue(id, out var window))
        {
            return;
        }

        var sizes = window.Members.Select(member => member.Pane.Size).ToList();
        if (sizes.Count == 0 || sizes.Exists(size => size.Columns <= 0 || size.Rows <= 0))
        {
            return;
        }

        var tabs = window.Members.Select(member => member.Tab).ToList();
        SplitNode<SessionViewModel>? root = null;
        if (tabs.Count > 1)
        {
            if (SplitOf(tabs[0]) is not { Fixed: true } group || group.Members.Count != tabs.Count || group.Members.Except(tabs).Any())
            {
                return;
            }

            root = group.Root;
        }

        var said = $"{(root is null ? string.Empty : TmuxShapeOf(root, tabs))}:{string.Join(';', sizes)}";
        if (window.Sized == said)
        {
            return;
        }

        window.Sized = said;
        Interlocked.Increment(ref link.SizingRounds);
        var atTmux = window.AtTmux;
        window.AtTmux = window.Members.ToDictionary(member => member.Pane.Pane, member => member.Pane.Size);
        if (root is null)
        {
            client.Resize(sizes[0].Columns, sizes[0].Rows);
            return;
        }

        var member = (SessionViewModel tab) => window.Members.First(candidate => ReferenceEquals(candidate.Tab, tab));
        Func<SessionViewModel, (int Columns, int Rows)?> wanted = tab => member(tab).Pane.Size;
        Func<SessionViewModel, (int Columns, int Rows)?> had = tab => atTmux.TryGetValue(member(tab).Pane.Pane, out var size) ? size : null;
        var (columns, rows) = TmuxExtent(root, wanted)!.Value;
        var before = TmuxExtent(root, had);
        client.Resize(columns, rows);

        // Ändert sich die Größe des Fensters, verteilt tmux das Mehr oder Weniger selbst auf die Bereiche – in dieser
        // Richtung ist danach nichts mehr, wie es war.
        ResizeTmuxBranches(client, root, wanted, had, tab => member(tab).Pane.Pane, before?.Columns != columns, before?.Rows != rows);
    }

    // Die Größe eines Knotens: ein Bereich ist so groß, wie sein Terminal meldet; null, wenn einer darin keine hat.
    private static (int Columns, int Rows)? TmuxExtent(SplitNode<SessionViewModel> node, Func<SessionViewModel, (int Columns, int Rows)?> sizeOf)
    {
        if (node is not SplitBranch<SessionViewModel> branch)
        {
            return sizeOf(((SplitLeaf<SessionViewModel>)node).Item);
        }

        var children = new List<(int Columns, int Rows)>();
        foreach (var child in branch.Children)
        {
            if (TmuxExtent(child, sizeOf) is not { } size)
            {
                return null;
            }

            children.Add(size);
        }

        return branch.SideBySide
            ? (children.Sum(child => child.Columns) + children.Count - 1, children.Max(child => child.Rows))
            : (children.Max(child => child.Columns), children.Sum(child => child.Rows) + children.Count - 1);
    }

    // Nennt tmux die Maße der Kinder eines Zweigs – aber nur, was dort nicht schon gilt. So rechnet tmux
    // (layout_resize_pane): Ein Kind, das wächst oder schrumpft, nimmt oder gibt beim nächsten dahinter, die davor
    // bleiben. Ändert sich ein Zweig in seiner eigenen Richtung, verteilt tmux das auf seine Kinder; quer dazu
    // bekommen alle dasselbe. Also: ab dem ersten Kind, das bei tmux anders ist als gewollt, alle bis zum
    // vorletzten – hat sich der Zweig selbst in seiner Richtung geändert („wide“, „tall“), alle. Jedes Kind ab dort
    // ist danach in dieser Richtung angefasst, und mit ihm alles darin.
    private static void ResizeTmuxBranches(
        TmuxClient client,
        SplitNode<SessionViewModel> node,
        Func<SessionViewModel, (int Columns, int Rows)?> wanted,
        Func<SessionViewModel, (int Columns, int Rows)?> had,
        Func<SessionViewModel, string> paneOf,
        bool wide,
        bool tall)
    {
        if (node is not SplitBranch<SessionViewModel> branch)
        {
            return;
        }

        int Along((int Columns, int Rows) size) => branch.SideBySide ? size.Columns : size.Rows;
        var count = branch.Children.Count;
        var first = (branch.SideBySide ? wide : tall) ? 0 : count;
        for (var index = 0; index < count - 1 && first == count; index++)
        {
            var child = branch.Children[index];
            if (TmuxExtent(child, had) is not { } before || Along(before) != Along(TmuxExtent(child, wanted)!.Value))
            {
                first = index;
            }
        }

        for (var index = first; index < count - 1; index++)
        {
            var child = branch.Children[index];

            // tmux ändert mit `resize-pane` die Zelle, in der der Bereich in einem Zweig dieser Richtung steht – der
            // Bereich muss deshalb unmittelbar im Kind stehen: das Kind selbst oder ein Blatt in dessen Zweig, der
            // quer verläuft. Ein Kind aus lauter Zweigen hat keinen; dort bleibt die Teilung tmux überlassen.
            var anchor = child switch
            {
                SplitLeaf<SessionViewModel> leaf => leaf.Item,
                SplitBranch<SessionViewModel> inner => inner.Children.OfType<SplitLeaf<SessionViewModel>>().FirstOrDefault()?.Item,
                _ => null,
            };
            if (anchor is null)
            {
                continue;
            }

            var (columns, rows) = TmuxExtent(child, wanted)!.Value;
            if (branch.SideBySide)
            {
                client.ResizePane(paneOf(anchor), columns, null);
            }
            else
            {
                client.ResizePane(paneOf(anchor), null, rows);
            }
        }

        for (var index = 0; index < count; index++)
        {
            var moved = index >= first;
            ResizeTmuxBranches(client, branch.Children[index], wanted, had, paneOf, wide || (branch.SideBySide && moved), tall || (!branch.SideBySide && moved));
        }
    }

    private void DropTmuxMember(TmuxLink link, TmuxWindow window, (SessionViewModel Tab, TmuxPane Pane) member)
    {
        window.Members.Remove(member);
        link.Panes.TryRemove(member.Pane.Pane, out _);
        member.Pane.Close(null);
        RemoveTmuxTab(member.Tab);
    }

    // Schließt der Benutzer den Tab eines tmux-Fensters, hängt die App ab – die Fenster laufen auf dem Server
    // weiter, und alle ihre Tabs schließen, sobald tmux das Ende meldet. Ein Fenster beendet man in ihm selbst
    // (exit), nicht am Reiter: Sonst nähme ein Klick laufende Programme mit.
    private bool ClosesWithTmux(SessionViewModel session)
    {
        if (_closingTmuxTab)
        {
            return false;
        }

        if (_tmux.FirstOrDefault(link => ReferenceEquals(link.Carrier, session)) is { } carried)
        {
            // Der Träger selbst schließt: Seine Fenster-Tabs gehen mit, er dann wie jeder Tab.
            EndTmux(carried, null);
            return false;
        }

        if (_tmux.FirstOrDefault(link => link.Tabs.Any(tab => ReferenceEquals(tab, session))) is not { } link)
        {
            return false;
        }

        link.Client?.Detach();
        Log(L.Get("TmuxDetaching", link.Name));
        return true;
    }

    private bool _closingTmuxTab;

    private void RemoveTmuxTab(SessionViewModel tab)
    {
        _closingTmuxTab = true;
        try
        {
            CloseSession(tab);
        }
        finally
        {
            _closingTmuxTab = false;
        }
    }

    // Der Steuermodus ist zu Ende (abgehängt, tmux beendet, Verbindung weg): Die Tabs der Fenster schließen, der
    // Träger ist wieder ein gewöhnliches Terminal.
    private void EndTmux(TmuxLink link, string? reason, bool started = true)
    {
        if (link.Ended)
        {
            return;
        }

        link.Ended = true;
        _tmux.Remove(link);
        link.Client?.Abandon();
        link.Carrier.OutputTap = null;
        link.Carrier.InputGate = null;
        link.Carrier.ConnectionEnded -= OnTmuxCarrierEnded;

        var back = link.Tabs.Any(tab => ReferenceEquals(tab, ActiveSession));
        foreach (var window in link.Windows.Values.ToList())
        {
            foreach (var (tab, pane) in window.Members.ToList())
            {
                pane.Close(reason);
                RemoveTmuxTab(tab);
            }
        }

        link.Windows.Clear();
        link.Panes.Clear();
        if (back && Sessions.Contains(link.Carrier))
        {
            ActiveSession = link.Carrier;
        }

        Log(
            started
                ? L.Get("TmuxEnded", Format.Visible(link.Carrier.Profile.Name), link.Name)
                : L.Get("TmuxNotStarted", Format.Visible(link.Carrier.Profile.Name), link.Name),
            started ? LogSeverity.Info : LogSeverity.Warning);
    }
}
