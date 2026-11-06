using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Windows.Storage.Pickers;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Controls;

/// <summary>
/// Hängt die Sitzungen aus <see cref="ShellViewModel.Sessions"/> an die Terminal-Seite im WebView2
/// (<c>Assets/terminal</c>). Ablauf je Sitzung: <c>open</c> → <c>layout</c> → die Seite meldet ihre Größe → erst
/// dann startet die Sitzung, damit die Shell von Anfang an die richtige Zeilenbreite kennt.
/// <para>
/// Was die Seite zeigt (ein Terminal oder mehrere geteilt), bestimmt <see cref="ShellViewModel.Layout"/>; Suche,
/// Farbschema und Schriftgröße kommen ebenfalls aus dem ViewModel und werden hier nur weitergereicht.
/// </para>
/// <para>
/// Ausgabe läuft über <see cref="TerminalOutputPump"/>: Sie fasst Blöcke zusammen und hält eine Sitzung an, wenn
/// die Seite mit dem Zeichnen nicht nachkommt.
/// </para>
/// </summary>
public sealed partial class TerminalHost : UserControl
{
    // .example ist reserviert (RFC 2606): Der Name kann nie auf einen echten Server zeigen.
    private const string HostName = "terminal.nexusssh.example";
    private const string Origin = "https://" + HostName;

    private readonly Dictionary<string, Attachment> _attached = new(StringComparer.Ordinal);
    private readonly TerminalOutputPump _pump;
    private bool _initializing;
    private bool _ready;

    /// <summary>Das Terminal des Hauptfensters – für den Selbsttest, der daran das Tempo misst.</summary>
    internal static TerminalHost? Current { get; private set; }

    // Je Sitzung der eine Hörer ihrer Ausgabe. Er bleibt, wenn ihr Terminal zwischen dem Hauptfenster und einem
    // eigenen Fenster umzieht – dort läuft eine zweite Seite, und unterwegs darf nichts verloren gehen.
    private static readonly Dictionary<string, TerminalFeed> Feeds = new(StringComparer.Ordinal);
    private static bool _watchesMoves;

    // Sitzungen, die diese Seite verlassen: Sie nennt noch ihren Inhalt, dann erst schließt ihr Terminal hier.
    private readonly Dictionary<string, TerminalFeed> _leaving = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _pageReady = new();
    private readonly SessionViewModel? _only;
    private bool _released;

    /// <summary>Wie lange eine Seite Zeit hat, den Inhalt eines Terminals zu nennen, das umzieht.</summary>
    private static readonly TimeSpan SnapshotWait = TimeSpan.FromSeconds(3);

    /// <summary>Die Sitzung hat diese Seite verlassen (ihr Inhalt ist übergeben) – ein eigenes Fenster schließt dann.</summary>
    internal event Action<SessionViewModel>? Left;

    /// <summary>Was die Seite von der Ausgabe dieser Sitzung noch nicht bestätigt hat, in Bytes.</summary>
    internal long Backlog(string id) => _pump.Backlog(id);

    /// <summary>Wertet in der Seite dieses Terminals etwas aus – nur für den Selbsttest.</summary>
    internal async Task<string> EvaluateAsync(string script) => _ready ? await WebView.CoreWebView2.ExecuteScriptAsync(script) : string.Empty;

    public TerminalHost()
        : this(null)
    {
    }

    /// <param name="only">
    /// Die eine Sitzung, deren Terminal dieser Host in einem eigenen Fenster zeigt; <c>null</c> für das Hauptfenster,
    /// das alle übrigen zeigt.
    /// </param>
    public TerminalHost(SessionViewModel? only)
    {
        _only = only;
        if (only is null)
        {
            Current = this;
        }

        // Vor allen Hosts: Ein Umzug beginnt, bevor der eine die Sitzung abgibt und der andere sie annimmt – gleich
        // in welcher Reihenfolge die beiden davon hören.
        if (!_watchesMoves)
        {
            _watchesMoves = true;
            Shell.DetachedChanged += session =>
            {
                if (Feeds.TryGetValue(session.Id, out var feed))
                {
                    feed.BeginMove();
                }
            };
        }

        InitializeComponent();
        _pump = new TerminalOutputPump(
            (id, data) => Post(TerminalMessages.Write(id, data.Span)),
            action => DispatcherQueue.TryEnqueue(() => action()),
            (id, paused) =>
            {
                if (_attached.TryGetValue(id, out var attachment))
                {
                    attachment.Session.Terminal.SetPaused(paused);
                }
            });
        Loaded += OnLoaded;
        Shell.Sessions.CollectionChanged += OnSessionsChanged;
        Shell.DetachedChanged += OnDetachedChanged;
        Shell.PropertyChanged += OnShellPropertyChanged;
        Shell.UiRequested += OnUiRequested;

        var search = Shell.Search;
        search.FindRequested += OnFindRequested;
        search.ClearRequested += OnFindCleared;

        // Die Liste aller Fundstellen gilt den Terminals des Hauptfensters: Zwei Seiten antworteten sonst
        // nacheinander, und die zweite Antwort ersetzte die erste.
        if (_only is null)
        {
            search.ListRequested += (query, matchCase) => Post(TerminalMessages.Grep(query, matchCase));
            search.RevealRequested += OnRevealRequested;
        }

        SyncSessions();
    }

    /// <summary>
    /// Das Fenster dieses Hosts schließt: Er hört auf nichts mehr. Was er noch zeigt, gibt er ohne Inhalt ab.
    /// </summary>
    internal void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        Shell.Sessions.CollectionChanged -= OnSessionsChanged;
        Shell.DetachedChanged -= OnDetachedChanged;
        Shell.PropertyChanged -= OnShellPropertyChanged;
        Shell.UiRequested -= OnUiRequested;
        Shell.Search.FindRequested -= OnFindRequested;
        Shell.Search.ClearRequested -= OnFindCleared;
        foreach (var (id, feed) in _leaving.ToList())
        {
            _leaving.Remove(id);
            feed.CompleteMove([]);
        }

        foreach (var attachment in _attached.Values.ToList())
        {
            var id = attachment.Session.Id;
            attachment.Session.PropertyChanged -= OnSessionChanged;
            _pump.Remove(id);
            if (Shell.Sessions.Contains(attachment.Session))
            {
                // Das Fenster ging zu, ohne dass die Sitzung umzog: Ihre Ausgabe wird aufgehoben, bis wieder
                // ein Terminal sie nimmt – statt in eine Pumpe zu laufen, die niemand mehr leert.
                attachment.Feed.BeginMove();
                attachment.Feed.CompleteMove([]);
            }
            else if (Feeds.Remove(id))
            {
                // Die Sitzung ist zu (ihr Fenster schloss, bevor dieser Host davon hörte): Niemand hört mehr zu.
                attachment.Feed.Dispose();
            }
        }

        _attached.Clear();
    }

    private void OnSessionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => SyncSessions();

    private void OnDetachedChanged(SessionViewModel session) => SyncSessions();

    private void OnFindRequested(FindRequest request)
        => PostToActive(id => TerminalMessages.Find(id, request.Query, request.MatchCase, request.Backwards, request.Incremental));

    private void OnFindCleared() => PostToActive(TerminalMessages.FindClear);

    private static ShellViewModel Shell => App.Shell;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _initializing = true;
        try
        {
            await WebView.EnsureCoreWebView2Async();

            // Ohne die Laufzeit scheitert das nicht – es gibt dann nur keinen Kern (gesehen 2026-10-12 auf einem
            // Windows ohne WebView2: „Object reference not set …“ statt eines Hinweises).
            if (WebView.CoreWebView2 is not { } core)
            {
                ShowUnavailable(L.Get("TerminalNeedsWebView"), missingRuntime: true);
                return;
            }

            var settings = core.Settings;
            settings.AreHostObjectsAllowed = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPinchZoomEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;

            // Die Seite zeigt keine eigenen Dialoge (alert, confirm): Ihr Text käme vom Server, und solange einer
            // offen ist, stehen alle Terminals.
            settings.AreDefaultScriptDialogsEnabled = false;
#if DEBUG
            settings.AreDevToolsEnabled = true;
            settings.AreDefaultContextMenusEnabled = true;
#else
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
#endif

            // Die Seite bleibt, wo sie ist: keine Navigation woandershin, keine neuen Fenster. Ein Link in der
            // Ausgabe eines Servers darf den WebView nicht aus der Terminal-Seite herausführen.
            core.NavigationStarting += (_, args) => args.Cancel = !args.Uri.StartsWith(Origin + "/", StringComparison.Ordinal);
            core.NewWindowRequested += (_, args) => args.Handled = true;

            // Die Seite bekommt nichts, wonach sie fragen könnte – keine Zwischenablage, keine Kamera, keinen Ort –,
            // und sie lädt nichts herunter. Beides braucht sie nicht; ohne diese Zeilen fragte WebView2 den Benutzer.
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true; core.WebMessageReceived += OnWebMessageReceived;

            core.SetVirtualHostNameToFolderMapping(
                HostName,
                Path.Combine(AppContext.BaseDirectory, "Assets", "terminal"),
                CoreWebView2HostResourceAccessKind.Deny);
            core.Navigate(Origin + "/index.html");
        }
        catch (Exception exception)
        {
            // Typisch: WebView2-Runtime fehlt oder der Datenordner ist nicht beschreibbar.
            var missing = RuntimeMissing();
            ShowUnavailable(missing ? L.Get("TerminalNeedsWebView") : exception.Message, missing);
        }
    }

    private void ShowUnavailable(string detail, bool missingRuntime)
    {
        WebView.Visibility = Visibility.Collapsed;
        ErrorDetail.Text = detail;
        GetWebViewLink.Visibility = missingRuntime ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    // Nennt Windows keine Version der Laufzeit (oder scheitert schon die Frage), ist sie nicht installiert.
    private static bool RuntimeMissing()
    {
        try
        {
            return string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        switch (TerminalMessages.Parse(args.WebMessageAsJson))
        {
            case WebMessage.Ready:
                // Kommt auch nach einem Neuladen der Seite: dann alles neu aufbauen. Was an die alte Seite
                // unterwegs war, bestätigt niemand mehr.
                _ready = true;
                _pump.Reset();
                PostOptions();
                PostBackdrop();
                foreach (var attachment in _attached.Values)
                {
                    Post(TerminalMessages.Open(attachment.Session.Id, MarkOf(attachment.Session), Shell.ThemeOverride(attachment.Session.Profile)));
                    PostLabel(attachment.Session);
                    PostNames(attachment.Session);
                }

                PostLayout();
                _pageReady.TrySetResult();
                break;

            // Nur als Antwort auf die Bitte beim Umzug, und nur für eine Sitzung, die gerade geht.
            case WebMessage.Snapshot snapshot when _leaving.ContainsKey(snapshot.Id):
                FinishLeaving(snapshot.Id, System.Text.Encoding.UTF8.GetBytes(snapshot.Data));
                break;

            case WebMessage.Resize resize when _attached.TryGetValue(resize.Id, out var attachment):
                attachment.Session.Terminal.Resize(resize.Columns, resize.Rows);
                attachment.Session.Terminal.Start();
                break;

            // Über die Shell: Sie reicht die Eingabe an die ganze geteilte Ansicht, wenn das eingeschaltet ist.
            case WebMessage.Input input when _attached.TryGetValue(input.Id, out var attachment):
                Shell.Input(attachment.Session, input.Data);
                break;

            case WebMessage.Bell bell when _attached.TryGetValue(bell.Id, out var attachment):
                Shell.OnTerminalBell(attachment.Session);
                break;

            case WebMessage.Notice notice when _attached.TryGetValue(notice.Id, out var attachment):
                Shell.OnTerminalNotice(attachment.Session, notice.Title, notice.Body);
                break;

            // Der Titel, den ein Programm auf dem Server gesetzt hat – die Sitzung säubert ihn und entscheidet, ob er
            // am Reiter steht.
            case WebMessage.TitleChanged title when _attached.TryGetValue(title.Id, out var attachment):
                attachment.Session.SetServerTitle(title.Title);
                break;

            case WebMessage.DirectoryChanged directory when _attached.TryGetValue(directory.Id, out var attachment):
                Shell.OnTerminalDirectory(attachment.Session, directory.Path);
                break;

            // Mit Strg angeklickt. Die Adresse ist schon geprüft (nur http und https) und geht als Adresse an
            // Windows, nie als Befehlszeile – sie stand in der Ausgabe eines Servers.
            case WebMessage.LinkActivated link when _attached.TryGetValue(link.Id, out var attachment):
                Shell.OnTerminalLink(attachment.Session, link.Link);
                _ = Windows.System.Launcher.LaunchUriAsync(link.Link);
                break;

            case WebMessage.PathActivated path when _attached.TryGetValue(path.Id, out var attachment):
                _ = Shell.OnTerminalPath(attachment.Session, path.Path);
                break;

            // Die Seite meldet die Kennung eines Befehls; einen unbekannten führt die Shell nicht aus.
            // „Nur das Terminal“ gilt dem Hauptfenster; aus einem abgelösten Fenster heraus schaltete F11 dort etwas
            // um, das der Benutzer gerade nicht sieht.
            case WebMessage.Shortcut { Key: "view.terminalOnly" } when _only is not null:
                break;

            case WebMessage.Shortcut shortcut:
                Shell.RunCommand(shortcut.Key);
                break;

            case WebMessage.Focused focused when _attached.TryGetValue(focused.Id, out var attachment):
                Shell.ActiveSession = attachment.Session;
                break;

            // Das × in der Kopfzeile eines geteilten Terminals. Die Sitzung bleibt offen – schließen kann die Seite nichts.
            case WebMessage.PaneClosed closed when _attached.TryGetValue(closed.Id, out var attachment):
                Shell.Unsplit(attachment.Session);
                break;

            // Eine Trennlinie wurde gezogen. Die Shell nimmt es nur für die Teilung, die zu sehen ist.
            case WebMessage.Shares shares when _attached.TryGetValue(shares.Id, out var attachment):
                Shell.SetShares(attachment.Session, shares.Path, shares.Values);
                break;

            // Ein Kopf wurde an die Kante eines anderen gezogen; unbekannte Kennungen gelten nicht.
            case WebMessage.Moved moved when _attached.TryGetValue(moved.Id, out var item) && _attached.TryGetValue(moved.Target, out var target):
                Shell.MovePane(item.Session, target.Session, moved.Side);
                break;

            // Nur wenn es eingestellt ist – die Seite fragt sonst nicht, und eine Bitte von ihr allein genügt nicht.
            case WebMessage.PasteRequested paste when Shell.MouseCopyPaste && _attached.ContainsKey(paste.Id):
                _ = PasteAsync(paste.Id);
                break;

            case WebMessage.PasteNeedsConsent ask when _attached.ContainsKey(ask.Id):
                _ = ConfirmPasteAsync(ask.Id, ask.Content);
                break;

            case WebMessage.Acknowledged acknowledged:
                _pump.Acknowledge(acknowledged.Id, acknowledged.Count);
                break;

            // Nur als Antwort auf „Ausgabe speichern“: Von sich aus öffnet die Seite keinen Speichern-Dialog.
            case WebMessage.Text text when _dumpAsked && _attached.TryGetValue(text.Id, out var attachment):
                _dumpAsked = false;
                _ = SaveTextAsync(attachment.Session, text.Data);
                break;

            // Nur als Antwort auf einen der beiden Kopier-Befehle: Von sich aus füllt die Seite so die Zwischenablage nicht.
            case WebMessage.Copied copied when _copyAsked && _attached.ContainsKey(copied.Id):
                _copyAsked = false;
                Copy(copied);
                break;

            case WebMessage.Found found when found.Id == Shell.ActiveSession?.Id:
                Shell.Search.ReportFound(found.Index, found.Count);
                break;

            case WebMessage.Hits hits:
                Shell.Search.ReportHits(
                    hits.Query,
                    [
                        .. hits.Items
                            .Where(item => _attached.ContainsKey(item.Id))
                            .Select(item => new SearchHit(item.Id, _attached[item.Id].Session.Title, item.Row, item.Text)),
                    ],
                    hits.More);
                break;
        }
    }

    // Gleicht mit der ganzen Liste ab statt mit dem einzelnen Ereignis: so stimmt es auch nach Reset oder Verschieben.
    private void SyncSessions()
    {
        if (_released)
        {
            return;
        }

        // Das Hauptfenster zeigt alle, deren Terminal nicht in einem eigenen Fenster steht; ein eigenes Fenster
        // genau seine eine Sitzung, solange sie dort steht.
        var current = Shell.Sessions
            .Where(session => _only is null ? !session.IsDetached : ReferenceEquals(session, _only) && session.IsDetached)
            .ToDictionary(session => session.Id, StringComparer.Ordinal);

        foreach (var id in _attached.Keys.Where(id => !current.ContainsKey(id)).ToList())
        {
            _attached.Remove(id, out var attachment);
            var session = attachment!.Session;
            session.PropertyChanged -= OnSessionChanged;
            if (Shell.Sessions.Contains(session))
            {
                Leave(session, attachment.Feed);
                continue;
            }

            // Die Sitzung ist zu: Niemand hört mehr zu.
            Feeds.Remove(id);
            attachment.Feed.Dispose();
            _pump.Remove(id);
            Post(TerminalMessages.Close(id));
        }

        var joined = false;
        foreach (var session in current.Values.Where(session => !_attached.ContainsKey(session.Id)))
        {
            joined = true;
            if (!Feeds.TryGetValue(session.Id, out var feed))
            {
                Feeds[session.Id] = feed = new TerminalFeed(session.Terminal);
            }

            var attachment = new Attachment(session, feed);
            _attached.Add(session.Id, attachment);
            session.PropertyChanged += OnSessionChanged;
            Post(TerminalMessages.Open(session.Id, MarkOf(session), Shell.ThemeOverride(session.Profile)));
            PostLabel(session);
            PostNames(session);
            _ = ConnectAsync(attachment);
        }

        // Was die Seite zeigen soll, stand womöglich schon fest, bevor sie das Terminal dazu kannte (ein Tab kehrt
        // aus seinem Fenster zurück: erst gilt die Anordnung, dann zieht das Terminal um).
        if (joined && (_only is not null || Shell.Layout.Panes.Any(session => current.ContainsKey(session.Id))))
        {
            PostLayout();
        }
    }

    // Schließt die Ausgabe der Sitzung an diese Seite an – erst, wenn die Seite steht und das vorige Terminal seinen
    // Inhalt genannt hat (ohne Umzug: sofort). Der Inhalt kommt zuerst, dann was unterdessen ausgegeben wurde.
    private async Task ConnectAsync(Attachment attachment)
    {
        var id = attachment.Session.Id;
        await _pageReady.Task;
        var first = await attachment.Feed.Moved;
        if (_released || !_attached.TryGetValue(id, out var now) || !ReferenceEquals(now, attachment))
        {
            // Unterdessen ist die Sitzung zu oder schon wieder weitergezogen.
            return;
        }

        // Ausgabe kann von einem beliebigen Thread kommen und der Puffer gilt nur während des Aufrufs: Die Pumpe
        // kopiert ihn und bringt ihn auf den UI-Thread.
        attachment.Feed.Attach(data => _pump.Add(id, data.Span), first);
    }

    // Die Sitzung zieht in ein anderes Fenster: Die Seite schreibt erst zu Ende, was noch wartet, und nennt dann
    // den Inhalt ihres Terminals. Die Ausgabe hebt unterdessen der Hörer auf (TerminalFeed.BeginMove).
    private void Leave(SessionViewModel session, TerminalFeed feed)
    {
        var id = session.Id;
        var waiting = _pump.Take(id);
        if (!_ready)
        {
            feed.CompleteMove([]);
            Left?.Invoke(session);
            return;
        }

        if (waiting.Length > 0)
        {
            Post(TerminalMessages.Write(id, waiting));
        }

        _leaving[id] = feed;
        Post(TerminalMessages.Snapshot(id));

        // Antwortet die Seite nicht, zieht die Sitzung ohne ihren Verlauf um – stehen bleibt sie nicht.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = SnapshotWait;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => FinishLeaving(id, []);
        timer.Start();
    }

    private void FinishLeaving(string id, byte[] content)
    {
        if (!_leaving.Remove(id, out var feed))
        {
            return;
        }

        Post(TerminalMessages.Close(id));
        feed.CompleteMove(content);
        if (Shell.Sessions.FirstOrDefault(session => session.Id == id) is { } session)
        {
            Left?.Invoke(session);
        }
    }

    // Name und Zustand stehen in der Kopfzeile des Terminals, wenn die Fläche geteilt ist.
    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is SessionViewModel session
            && e.PropertyName is nameof(SessionViewModel.Title) or nameof(SessionViewModel.State) or nameof(SessionViewModel.PathNames) or nameof(SessionViewModel.CommandNames)
            && _attached.ContainsKey(session.Id))
        {
            // Zum Ergänzen beim Tippen bekommt die Seite Listen – nie eine Frage, und sie stellt auch keine.
            switch (e.PropertyName)
            {
                case nameof(SessionViewModel.PathNames):
                    Post(TerminalMessages.Names(session.Id, session.PathNames));
                    break;
                case nameof(SessionViewModel.CommandNames):
                    Post(TerminalMessages.Commands(session.Id, session.CommandNames));
                    break;
                default:
                    PostLabel(session);
                    break;
            }
        }
    }

    // Ein Terminal, das (neu) auf der Seite steht, bekommt die Listen, die seine Sitzung schon kennt.
    private void PostNames(SessionViewModel session)
    {
        if (session.PathNames.Count > 0)
        {
            Post(TerminalMessages.Names(session.Id, session.PathNames));
        }

        if (session.CommandNames.Count > 0)
        {
            Post(TerminalMessages.Commands(session.Id, session.CommandNames));
        }
    }

    // Die Bereiche eines Fensters von tmux ordnet tmux – sie bekommen kein ×.
    private void PostLabel(SessionViewModel session)
        => Post(TerminalMessages.Label(session.Id, session.Title, session.State, session.IsVirtual ? null : L.Get("PaneClose")));

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.Layout):
                PostLayout();
                break;

            case nameof(ShellViewModel.Themes):
                // Ein Schema wurde geändert oder entfernt: Terminals mit eigenem Schema bekommen es neu.
                foreach (var attachment in _attached.Values)
                {
                    Post(TerminalMessages.Theme(attachment.Session.Id, Shell.ThemeOverride(attachment.Session.Profile)));
                }

                ApplyCardBackground();
                break;

            case nameof(ShellViewModel.ActiveSession):
                ApplyCardBackground();
                break;

            case nameof(ShellViewModel.TerminalBackgroundImage) or nameof(ShellViewModel.TerminalBackgroundDim):
                PostBackdrop();
                break;

            case nameof(ShellViewModel.TerminalTheme) or nameof(ShellViewModel.TerminalFontSize) or nameof(ShellViewModel.TerminalFont) or nameof(ShellViewModel.MouseCopyPaste) or nameof(ShellViewModel.Highlights) or nameof(ShellViewModel.Snippets) or nameof(ShellViewModel.SnippetSuggestions) or nameof(ShellViewModel.OptionSuggestions) or nameof(ShellViewModel.PathSuggestions) or nameof(ShellViewModel.TerminalHosts) or nameof(ShellViewModel.TabCompletes) or nameof(ShellViewModel.TerminalShortcuts) or nameof(ShellViewModel.TerminalLook):
                PostOptions();
                break;
        }
    }

    private void OnUiRequested(UiRequest request)
    {
        switch (request)
        {
            case UiRequest.ClearTerminal:
                PostToActive(TerminalMessages.Clear);
                break;

            // Die Seite setzt den Fokus auf die Linie – das wirkt nur, wenn das WebView selbst ihn hat.
            case UiRequest.FocusDivider when Shell.ActiveSession is { } active && _attached.ContainsKey(active.Id):
                FocusWebView();
                Post(TerminalMessages.FocusDivider(active.Id));
                break;

            // Die Seite antwortet mit dem Text; gespeichert wird in SaveTextAsync.
            case UiRequest.SaveTerminal:
                _dumpAsked = true;
                PostToActive(TerminalMessages.Dump);
                break;

            // Auch hier antwortet die Seite: mit der Auswahl, die dann die App in die Zwischenablage legt.
            case UiRequest.CopyFormatted:
                _copyAsked = true;
                PostToActive(TerminalMessages.CopyRich);
                break;

            case UiRequest.CopyOneLine:
                _copyAsked = true;
                PostToActive(TerminalMessages.CopyLine);
                break;

            // Nur die Seite, die das Terminal des aktiven Tabs zeigt.
            case UiRequest.FocusTerminal when Shell.ActiveSession is { } active && _attached.ContainsKey(active.Id):
                FocusWebView();
                break;

            case UiRequest.FocusTerminal when _only is null && Shell.ActiveSession is null:
                FocusWebView();
                break;

            case UiRequest.ToggleLog when Shell.ActiveSession is { } session && _attached.ContainsKey(session.Id):
                _ = ToggleLogAsync(session);
                break;
        }
    }

    // Eine Fundstelle aus der Liste: erst ihre Sitzung nach vorn holen, dann dorthin rollen.
    private void OnRevealRequested(SearchHit hit, string query, bool matchCase)
    {
        if (_attached.TryGetValue(hit.SessionId, out var attachment))
        {
            Shell.ActiveSession = attachment.Session;
            Post(TerminalMessages.Reveal(hit.SessionId, hit.Row, query, matchCase));
            FocusWebView();
        }
    }

    private void PostLayout()
    {
        if (_only is not null)
        {
            // Ein eigenes Fenster zeigt immer seine eine Sitzung; den Fokus holt es sich nicht von selbst.
            if (_attached.ContainsKey(_only.Id))
            {
                Post(TerminalMessages.Layout(new SplitLeaf<string>(_only.Id), _only.Id, null, @fixed: false));
            }

            return;
        }

        // Nur die Bereiche, die die Seite kennt: Was noch nicht angeschlossen ist, fällt aus dem Baum (der Rest
        // rückt zusammen), und kommt nach dem Anschließen mit der nächsten Anordnung.
        var layout = Shell.Layout;
        var tree = layout.Tree;
        foreach (var session in layout.Panes.Where(session => !_attached.ContainsKey(session.Id)))
        {
            tree = tree is null ? null : SplitTree.Remove(tree, session);
        }

        var shown = tree is null ? [] : SplitTree.Leaves(tree);
        var zoomed = layout.Zoomed is { } zoom && shown.Contains(zoom) ? zoom.Id : null;
        Post(TerminalMessages.Layout(
            tree is null ? null : SplitTree.Map(tree, session => session.Id),
            layout.Active?.Id,
            zoomed,
            layout.Fixed));
        if (_ready && shown.Count > 0)
        {
            FocusWebView();
        }
    }

    // Rechtsklick im Terminal: Text der Zwischenablage an die Seite, die ihn über xterm einfügt.
    // Das Bild hinter den Terminals: Die Datei liest die App (die Seite hat keinen Zugriff auf die Platte), die Prüfung
    // und die Form für die Seite macht Core. Lässt sich die Datei nicht lesen oder ist sie kein Bild, gibt es keines.
    private void PostBackdrop()
    {
        if (!_ready)
        {
            return;
        }

        string? image = null;
        if (Shell.TerminalBackgroundImage is { Length: > 0 } path)
        {
            try
            {
                var file = new FileInfo(path);
                image = file.Exists && file.Length <= TerminalBackdrop.MaxBytes ? TerminalBackdrop.DataUrl(File.ReadAllBytes(path)) : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                App.WriteCrashLog(exception);
            }
        }

        Post(TerminalMessages.Backdrop(image, Shell.TerminalBackgroundDim));
    }

    // Die Kennfarbe des Servers (seine eigene oder die seiner Gruppe) für den Streifen am Rand des Terminals.
    private static string? MarkOf(SessionViewModel session) => ProfileColors.Find(session.Profile.ShownColor).Hex;

    private async Task PasteAsync(string id)
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                Post(TerminalMessages.Paste(id, await content.GetTextAsync()));
            }
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Die Zwischenablage gehört gerade einem anderen Programm; dann wird eben nichts eingefügt.
        }
    }

    // Mehrere Zeilen in eine Shell, die jede sofort ausführt: erst fragen. Der Text geht danach mit dem Vermerk
    // zurück an die Seite, die ihn wie jedes Einfügen behandelt.
    private async Task ConfirmPasteAsync(string id, string text)
    {
        var (lines, preview) = TerminalMessages.PastePreview(text);
        if (await App.Dialogs.ConfirmAsync(L.Get("PasteLinesTitle", lines), $"{L.Get("PasteLinesText")}\n\n{preview}", L.Get("PasteLinesConfirm"))
            && _attached.ContainsKey(id))
        {
            Post(TerminalMessages.Paste(id, text, confirmed: true));
        }
    }

    // Die App hat die Seite um den Text eines Terminals gebeten und wartet auf die Antwort.
    private bool _dumpAsked;
    private bool _copyAsked;

    // Legt die Auswahl in die Zwischenablage: als Text, und mit Formatierung zusätzlich als HTML und RTF – beides
    // baut Core aus den geprüften Stücken (RichCopy), nie die Seite.
    private void Copy(WebMessage.Copied copied)
    {
        if (copied.Content.Length == 0)
        {
            Shell.Log(L.Get("CopyNothingSelected"), LogSeverity.Warning);
            return;
        }

        byte[]? html = null;
        string? rtf = null;
        if (copied is { OneLine: false, Runs: { } runs })
        {
            var theme = Shell.TerminalTheme;
            var (fore, back) = (copied.Foreground ?? theme.Foreground, copied.Background ?? theme.Background);
            html = RichCopy.ClipboardHtml(RichCopy.Html(runs, Shell.TerminalFont, Shell.TerminalFontSize, fore, back));
            rtf = RichCopy.Rtf(runs, Shell.TerminalFont, Shell.TerminalFontSize, fore, back);
        }

        // Nicht über die Zwischenablage von WinUI: Deren RTF überlebte das Ende der App nicht (TextClipboard).
        var window = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        if (Interop.TextClipboard.Set(window, copied.Content, html, rtf))
        {
            Shell.Log(L.Get(copied.OneLine ? "CopiedOneLine" : "CopiedFormatted", copied.Content.Length));
        }
        else
        {
            // Die Zwischenablage gehört gerade einem anderen Programm.
            Shell.Log(L.Get("CopyFailed", "clipboard in use"), LogSeverity.Warning);
        }
    }

    private void PostOptions()
    {
        Post(TerminalMessages.Options(Shell.TerminalTheme, Shell.TerminalFontSize, Shell.TerminalShortcuts, Shell.TerminalFont, Shell.MouseCopyPaste, Shell.HighlightRules, Shell.TerminalSuggestions, L.Get("TerminalLinkHint"), Shell.TerminalLook, Shell.PasteClean, Shell.PasteTabs, Shell.TerminalImages, Shell.OptionSuggestions, Shell.PathSuggestions, Shell.TerminalHosts, Shell.TabCompletes));
        ApplyCardBackground();
    }

    // Die Karte um das Terminal hat die Farbe des Schemas, das gerade zu sehen ist – das des aktiven Tabs.
    private void ApplyCardBackground()
    {
        if (_only is not null)
        {
            // Die Karte des Hauptfensters gehört dem Hauptfenster; hier zählt das Schema dieser einen Sitzung.
            WebView.DefaultBackgroundColor = Ui.ParseColor(Shell.ThemeFor(_only.Profile).Background);
            return;
        }

        Ui.ApplyTerminalBackground(Shell.ShownTheme.Background);
        WebView.DefaultBackgroundColor = Ui.ParseColor(Shell.ShownTheme.Background);
    }

    /// <summary>Setzt den Fokus in das Terminal dieser Seite.</summary>
    internal void FocusTerminal() => FocusWebView();

    // Den Fokus nur setzen, wenn das Fenster dieser Seite schon vorn ist: Fokus in einem anderen Fenster derselben
    // App holt Windows nach vorn. So nahm sich das Hauptfenster den Vordergrund, während ein abgelöstes Terminal
    // vorn war und seine Verbindung neu aufbaute – und eine Rückfrage ging dann im falschen Fenster auf (gefunden
    // 2026-10-14).
    private void FocusWebView()
    {
        if (XamlRoot?.ContentIslandEnvironment is { } island
            && !Interop.Attention.IsInFront(Microsoft.UI.Win32Interop.GetWindowFromWindowId(island.AppWindowId)))
        {
            return;
        }

        WebView.Focus(FocusState.Programmatic);
    }
    private void PostToActive(Func<string, string> message)
    {
        if (Shell.ActiveSession is { } session && _attached.ContainsKey(session.Id))
        {
            Post(message(session.Id));
        }
    }

    // Mitschrift der Ausgabe ein- oder ausschalten. Wohin, fragt der Speichern-Dialog – jedes Mal: Mitgeschrieben
    // wird nur auf ausdrücklichen Wunsch, nie von selbst.
    private async Task ToggleLogAsync(SessionViewModel session)
    {
        if (session.IsLogging)
        {
            session.StopLogging();
            return;
        }

        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = ShellViewModel.TerminalFileName(session, DateTimeOffset.Now),
            DefaultFileExtension = ".log",
        };
        picker.FileTypeChoices.Add(L.Get("TextFiles"), [".log", ".txt"]);
        if (await picker.PickSaveFileAsync() is { } file)
        {
            session.StartLogging(file.Path);
        }
    }

    // Der Verlauf eines Terminals als Textdatei. Wohin, fragt der Speichern-Dialog von Windows.
    private async Task SaveTextAsync(SessionViewModel session, string text)
    {
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = ShellViewModel.TerminalFileName(session, DateTimeOffset.Now),
            DefaultFileExtension = ".txt",
        };
        picker.FileTypeChoices.Add(L.Get("TextFiles"), [".txt", ".log"]);
        if (await picker.PickSaveFileAsync() is { } file)
        {
            Shell.SaveTerminalText(session, file.Path, text);
        }
    }

    private void Post(string json)
    {
        if (_ready)
        {
            WebView.CoreWebView2.PostWebMessageAsJson(json);
        }
    }

    private sealed record Attachment(SessionViewModel Session, TerminalFeed Feed);
}
