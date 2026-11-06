using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;
using Windows.UI;

namespace NexusSSH.App;

/// <summary>Kleine Umrechnungen für <c>{x:Bind app:Ui.…}</c>, die WinUI-Typen brauchen (alles andere steht in Core).</summary>
public static class Ui
{
    /// <summary>
    /// Der Presenter eines normalen Fensters; <c>null</c>, wenn es gerade bildschirmf\u00FCllend ist. Nie mit
    /// <c>Presenter is OverlappedPresenter</c> fragen: Das ist unter Native AOT immer falsch, im gew\u00F6hnlichen Build nicht.
    /// So blieben Fenstergr\u00F6\u00DFe, Mindestgr\u00F6\u00DFe und Maximieren in der ausgelieferten Fassung wirkungslos (gefunden 2026-10-31).
    /// </summary>
    public static Microsoft.UI.Windowing.OverlappedPresenter? Overlapped(Microsoft.UI.Windowing.AppWindow window)
        => window.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped
            ? WinRT.CastExtensions.As<Microsoft.UI.Windowing.OverlappedPresenter>(window.Presenter)
            : null;

    /// <summary>Der plattformfreie Name einer Taste f\u00FCr <see cref="FileListKeys"/>; leer, wenn keine der dort bekannten.</summary>
    public static string KeyName(Windows.System.VirtualKey key) => key switch
    {
        Windows.System.VirtualKey.Enter => "Enter",
        Windows.System.VirtualKey.F2 => "F2",
        Windows.System.VirtualKey.F4 => "F4",
        Windows.System.VirtualKey.F5 => "F5",
        Windows.System.VirtualKey.Delete => "Delete",
        Windows.System.VirtualKey.Back => "Back",
        Windows.System.VirtualKey.Up => "Up",
        Windows.System.VirtualKey.Left => "Left",
        Windows.System.VirtualKey.Right => "Right",
        Windows.System.VirtualKey.A => "A",
        Windows.System.VirtualKey.D => "D",
        Windows.System.VirtualKey.F => "F",
        Windows.System.VirtualKey.L => "L",
        _ => string.Empty,
    };

    /// <summary>Ob die Taste gerade gedr\u00FCckt ist.</summary>
    public static bool IsDown(Windows.System.VirtualKey key)
        => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>Was das Ereignis in einer Dateiliste bewirkt (<see cref="FileListKeys"/>), mit den Umschalttasten, wie sie jetzt stehen.</summary>
    public static FileListAction FileListActionFor(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e, bool remote)
        => FileListKeys.ActionFor(
            KeyName(e.Key),
            IsDown(Windows.System.VirtualKey.Control),
            IsDown(Windows.System.VirtualKey.Shift),
            IsDown(Windows.System.VirtualKey.Menu),
            remote);

    private const string FolderGlyph = "\uE8B7";
    private const string FileGlyph = "\uE7C3";

    // Pipe, Gerät, Socket: nichts, was sich öffnen ließe.
    private const string SpecialGlyph = "\uE9CE";

    // Ein Link ins Leere: Segoe Fluent Icons hat kein Zeichen für eine zerbrochene Kette – das Warndreieck liest sich
    // am ehesten als „hier stimmt etwas nicht“; was es ist, sagt der Tooltip (DanglingTip).
    private const string DanglingGlyph = "\uE7BA";

    /// <summary>
    /// Ordner, Datei, bei einem symbolischen Link das Link-Symbol – bei einem, der ins Leere zeigt, ein Warnzeichen –
    /// und ein eigenes für Pipes, Geräte und Sockets.
    /// </summary>
    public static string EntryGlyph(bool isDirectory, bool isLink, bool isSpecial, bool isDangling)
        => isLink ? (isDangling ? DanglingGlyph : "\uE71B") : isSpecial ? SpecialGlyph : isDirectory ? FolderGlyph : FileGlyph;

    /// <summary>Tooltip und Name des Symbols eines Links, der ins Leere zeigt; sonst keiner (<c>null</c>: dann gilt der Tooltip der Zeile).</summary>
    public static string? DanglingTip(bool isDangling) => isDangling ? L.Get("LinkDangling") : null;

    /// <summary>Symbol eines lokalen Eintrags: Laufwerk, Ordner oder Datei.</summary>
    public static string LocalGlyph(bool isDirectory, bool isDrive) => isDrive ? "" : isDirectory ? FolderGlyph : FileGlyph;

    public static Brush StatusBrush(bool online) => Resource(online ? "StatusOnlineBrush" : "StatusOfflineBrush");

    /// <summary>
    /// Der Punkt am Reiter (Entwurf A): grün, solange der Server antwortet, gelb, während die Sitzung verbindet oder es
    /// nach einem Abriss von selbst neu versucht (<paramref name="retryText"/> nicht leer), sonst grau.
    /// </summary>
    public static Brush TabDot(bool online, bool busy, string retryText)
        => Resource(online ? "StateOkBrush" : busy || retryText.Length > 0 ? "StateWaitBrush" : "StatusOfflineBrush");

    /// <summary>Farbe eines Balkens der Belegung: gewöhnlich die Akzentfarbe, voll werdend gelb, fast voll rot.</summary>
    public static Brush UsageBrush(UsageLevel level) => Resource(level switch
    {
        UsageLevel.Critical => "SystemFillColorCriticalBrush",
        UsageLevel.Warning => "SystemFillColorCautionBrush",
        _ => "AccentFillColorDefaultBrush",
    });

    /// <summary>Farbe einer Zeile eines Protokolls: Fehler rot, Warnungen gelb, alles andere wie gewöhnlicher Text.</summary>
    public static Brush LogBrush(UsageLevel level) => Resource(level switch
    {
        UsageLevel.Critical => "SystemFillColorCriticalBrush",
        UsageLevel.Warning => "SystemFillColorCautionBrush",
        _ => "TextFillColorPrimaryBrush",
    });

    /// <summary>Höhe des Balkens für einen Kern im Inspector.</summary>
    public const double CoreBarHeight = 28;

    /// <summary>Wie hoch der Balken eines Kerns bei dieser Auslastung ist – mindestens ein Strich, damit man ihn sieht.</summary>
    public static double CoreBar(double percent) => Math.Max(1, Math.Clamp(percent, 0, 100) / 100 * CoreBarHeight);

    public static string TransferGlyph(bool isUpload) => isUpload ? "\uE898" : "\uE896";

    public static string ProfileGlyph(bool favorite) => favorite ? "\uE735" : "\uE756";

    /// <summary>Eine ganze Zahl f\u00FCr ein Feld, das mit <c>double</c> rechnet (NumberBox).</summary>
    public static double ToDouble(int value) => value;

    /// <summary>Pfeil vor der \u00DCberschrift einer Gruppe: nach rechts, wenn sie eingeklappt ist.</summary>
    public static Controls.LineIconKind CollapseIcon(bool collapsed) => collapsed ? Controls.LineIconKind.ChevronRight : Controls.LineIconKind.ChevronDown;

    // Kn\u00F6pfe an der \u00DCberschrift einer Gruppe hei\u00DFen f\u00FCr Screenreader nach der Gruppe \u2013 sonst hie\u00DFen alle gleich.
    public static string GroupToggleName(string group) => $"{L.Get("GroupCollapse")}: {group}";

    public static string GroupOpenName(string group) => $"{L.Get("GroupOpen")}: {group}";

    public static string GroupActionsName(string group) => $"{L.Get("GroupActions")}: {group}";

    // Dasselbe für Knöpfe, die in jeder Zeile einer Liste stehen: Der Name nennt, wozu sie gehören.
    public static string MoreName(string what) => $"{L.Get("MoreActions")}: {what}";

    public static string ConnectName(string server) => $"{L.Get("Connect")}: {server}";

    public static string RunSyncName(string sync) => $"{L.Get("SyncRun")}: {sync}";

    public static string RenameSyncName(string sync) => $"{L.Get("Rename")}: {sync}";

    public static string DeleteSyncName(string sync) => $"{L.Get("Delete")}: {sync}";

    /// <summary>Der Schalter eines Tunnels – nach dem Tunnel benannt, damit eine Sprachausgabe sagt, welcher.</summary>
    public static string TunnelSwitchName(string tunnel) => L.Get("TunnelSwitch", tunnel);

    /// <summary>Die Zeile unter dem Namen eines Tunnels: Server, Art, Strecke – und ob er für das ganze Netz offen ist.</summary>
    public static string TunnelLine(string server, string kind, string route, bool open)
        => open ? $"{server} · {kind} · {route} · {L.Get("TunnelOpenBadge")}" : $"{server} · {kind} · {route}";

    /// <summary>Der Punkt vor einem Tunnel: grün, wenn er steht; rot, wenn er nicht zustande kam; sonst blass.</summary>
    public static Brush TunnelBrush(NexusSSH.Core.Models.TunnelState state) => Resource(state switch
    {
        NexusSSH.Core.Models.TunnelState.Listening => "StatusOnlineBrush",
        NexusSSH.Core.Models.TunnelState.Failed => "SystemFillColorCriticalBrush",
        _ => "StatusOfflineBrush",
    });

    /// <summary>Was in der Übersicht in der Zeile einer Sitzung steht, solange sie keine Werte hat.</summary>
    public static string OverviewNote(bool measurable, NexusSSH.Core.Services.ConnectionState state, bool hasMetrics, bool unavailable)
        => ShellViewModel.OverviewNote(measurable, state, hasMetrics, unavailable);

    /// <summary>Der Hinweis an der Stelle eines Terminals, das in einem eigenen Fenster steht.</summary>
    public static string DetachedNote(string? name) => L.Get("DetachedNote", name ?? string.Empty);

    public static string CancelTransferName(string file) => $"{L.Get("CancelTransfer")}: {file}";

    public static string RetryTransferName(string file) => $"{L.Get("RetryTransfer")}: {file}";

    public static string ResetShortcutName(string command) => $"{L.Get("ShortcutReset")}: {command}";

    public static string ChangeShortcutName(string command) => $"{L.Get("ShortcutChange")}: {command}";

    public static string ForgetHostName(string host) => $"{L.Get("ForgetHost")}: {host}";

    /// <summary>Name eines Reiters für eine Sprachausgabe: der Titel und, wenn die Sitzung es von selbst neu versucht, was sie gerade tut.</summary>
    public static string TabName(string title, string retry) => retry.Length > 0 ? $"{title}, {retry}" : title;

    /// <summary>
    /// Tooltip eines Reiters: die Adresse und, wie es um die Verbindung steht – ein selbsttätiger Versuch vor dem
    /// Zustand. Der Punkt am Reiter zeigt das nur als Farbe.
    /// </summary>
    public static string TabTip(string address, string state, string retry)
        => retry.Length > 0 ? $"{address} · {retry}" : state.Length > 0 ? $"{address} · {state}" : address;

    /// <summary>Wie ein Format von Schlüsseldateien heißt – Namen von Programmen, in jeder Sprache gleich.</summary>
    public static string KeyFormatName(KeyFormat format) => format switch
    {
        KeyFormat.Ppk => "PuTTY (.ppk)",
        KeyFormat.Pem => "PEM",
        _ => "OpenSSH",
    };

    /// <summary>Zeile unter dem Namen einer Schlüsseldatei: Art, Format und ob eine Passphrase sie schützt.</summary>
    public static string KeySummary(string kind, KeyFormat format, bool isEncrypted)
        => $"{(kind.Length > 0 ? kind : L.Get("KeyKindUnknown"))}  ·  {KeyFormatName(format)}  ·  {(isEncrypted ? L.Get("KeyWithPassphrase") : L.Get("KeyWithoutPassphrase"))}";

    /// <summary>Kommentar eines Schlüssels und wer ihn benutzt; leer, wenn es beides nicht gibt.</summary>
    public static string KeyNote(string comment, string usedBy)
        => string.Join("  ·  ", new[] { comment, usedBy.Length > 0 ? L.Get("KeyUsedBy", usedBy) : string.Empty }.Where(part => part.Length > 0));

    public static string KeyCopyName(string name) => $"{L.Get("KeyCopyPublic")}: {name}";

    public static string KeyPassphraseName(string name) => $"{L.Get("KeyChangePassphrase")}: {name}";

    /// <summary>Knopf „umwandeln“ einer Schlüsseldatei: Er nennt das Format, in das es geht.</summary>
    public static string KeyConvertText(KeyFormat format) => format == KeyFormat.Ppk ? L.Get("KeyConvertToOpenSsh") : L.Get("KeyConvertToPpk");

    public static Visibility KeyNoteVisibility(string comment, string usedBy) => VisibleIf(comment.Length > 0 || usedBy.Length > 0);

    public static string KeyConvertName(KeyFormat format, string name) => $"{KeyConvertText(format)}: {name}";

    /// <summary>Zeile der bekannten Server: der Name – oder, bei einer Zertifizierungsstelle, für welche Server sie gilt.</summary>
    public static string KnownHostTitle(string name, bool isAuthority) => isAuthority ? L.Get("AuthorityFor", name) : name;

    /// <summary>Symbol einer Zeile der Befehlspalette: Server, offener Tab, gespeicherter Befehl, Befehl der App.</summary>
    public static string PaletteGlyph(PaletteKind kind) => kind switch
    {
        PaletteKind.Snippet => "\uE943",
        PaletteKind.Server => "\uE756",
        PaletteKind.Tab => "\uE8AB",
        _ => "\uE945",
    };

    public static string SeverityGlyph(bool isError) => isError ? "\uE783" : "\uE7BA";

    public static Brush SeverityBrush(bool isError)
        => Resource(isError ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush");

    // Breite und Sichtbarkeit einer Spalte für die Zeilen der Dateilisten. Die Spalte kommt als Zahl, weil x:Bind
    // in einem Funktionsaufruf keine Aufzählung nennen kann: 1 Größe, 2 Datum, 3 Rechte, 4 Besitzer, 5 Gruppe, 6 Art
    // (die Werte von FileColumn, festgehalten in FileColumnsTests). Jede Zeile liest das beim Entstehen; nach einer
    // Änderung ziehen die gebauten Zeilen über ApplyColumns nach (Tag der Zelle = die Spalte).

    /// <summary>Wie breit die Spalte in der Liste des Servers ist, in Pixeln.</summary>
    public static double RemoteColumnWidth(int column) => App.Shell.RemoteColumns.WidthOf((FileColumn)column);

    /// <summary>Ob die Liste des Servers die Spalte zeigt.</summary>
    public static Visibility RemoteColumnVisibility(int column) => VisibleIf(App.Shell.RemoteColumns.IsVisible((FileColumn)column));

    /// <summary>Wie breit die Spalte in der lokalen Liste ist, in Pixeln.</summary>
    public static double LocalColumnWidth(int column) => App.Shell.LocalColumns.WidthOf((FileColumn)column);

    /// <summary>Ob die lokale Liste die Spalte zeigt.</summary>
    public static Visibility LocalColumnVisibility(int column) => VisibleIf(App.Shell.LocalColumns.IsVisible((FileColumn)column));

    // Die schon gebauten Zeilen einer Dateiliste auf den Stand der Spalten bringen, ohne die Liste anzufassen (Auswahl
    // und Rollstand bleiben). Die Zellen einer Zeile nennen ihre Spalte im Tag (der Wert von FileColumn als Text).

    /// <summary>Breite und Sichtbarkeit aller Spalten in allen gerade gebauten Zeilen.</summary>
    public static void ApplyColumns(ListViewBase list, FileColumns columns)
    {
        if (list.ItemsPanelRoot is { } panel)
        {
            foreach (var container in panel.Children)
            {
                ApplyColumnsToRow(container, columns);
            }
        }
    }

    /// <summary>
    /// Nach einem Sprung per Tippen (<c>SelectedIndex</c> allein verschiebt den Tastaturfokus nicht): rollt zum Eintrag und
    /// setzt den Fokus auf dessen Zeile, damit Pfeil und Umschalt+Pfeil von dort weitergehen. Nur, wenn die Liste ihn
    /// schon hat – nie Fokus in ein Fenster, das nicht vorn ist.
    /// </summary>
    public static void FocusRow(ListViewBase list, object item, int index)
    {
        list.ScrollIntoView(item);
        var focused = FocusManager.GetFocusedElement(list.XamlRoot) as DependencyObject;
        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, list))
            {
                list.UpdateLayout();
                (list.ContainerFromIndex(index) as Control)?.Focus(FocusState.Keyboard);
                return;
            }
        }
    }

    /// <summary>Breite und Sichtbarkeit der Spalten in einer Zeile (ein <c>ListViewItem</c>); eine andere Art Element wird übergangen.</summary>
    public static void ApplyColumnsToRow(DependencyObject container, FileColumns columns)
    {
        foreach (var (column, cell) in CellsOf(container))
        {
            cell.Width = columns.WidthOf(column);
            cell.Visibility = VisibleIf(columns.IsVisible(column));
        }
    }

    /// <summary>Eine Breite in allen gerade gebauten Zeilen – während des Ziehens, bevor sie gemerkt ist.</summary>
    public static void ApplyColumnWidth(ListViewBase list, FileColumn column, double width)
    {
        if (list.ItemsPanelRoot is { } panel)
        {
            foreach (var container in panel.Children)
            {
                foreach (var (other, cell) in CellsOf(container))
                {
                    if (other == column)
                    {
                        cell.Width = width;
                    }
                }
            }
        }
    }

    private static IEnumerable<(FileColumn Column, FrameworkElement Cell)> CellsOf(DependencyObject container)
    {
        if (container is not ListViewItem { ContentTemplateRoot: Grid row })
        {
            yield break;
        }

        foreach (var child in row.Children)
        {
            if (child is FrameworkElement { Tag: string tag } cell && int.TryParse(tag, out var number) && Enum.IsDefined((FileColumn)number))
            {
                yield return ((FileColumn)number, cell);
            }
        }
    }

    /// <summary>
    /// Rand des Symbols vor einem Server: seine Kennfarbe, sonst der gewöhnliche Rand. Die Farbe kommt als Kennung
    /// aus dem Profil (<see cref="ProfileColors"/>); eine unbekannte ist keine.
    /// </summary>
    public static Brush ProfileBorder(string? color)
        => ProfileColors.Find(color).Hex is { } hex ? new SolidColorBrush(ParseColor(hex)) : Resource("PillSelectedBorderBrush");

    /// <summary>Die Kennfarbe eines Profils als Fläche, z. B. für den Streifen am Reiter; ohne Farbe durchsichtig.</summary>
    public static Brush ProfileFill(string? color)
        => new SolidColorBrush(ProfileColors.Find(color).Hex is { } hex ? ParseColor(hex) : Microsoft.UI.Colors.Transparent);

    /// <summary>
    /// Das Zeichen des Betriebssystems für das Symbol vor einem Server; <c>null</c>, solange es unbekannt ist. Jeder
    /// Aufruf ergibt eine eigene Geometrie – zwei Elemente können sich keine teilen.
    /// </summary>
    public static Geometry? SystemIcon(string? system)
        => ServerSystems.Find(system) is not null && ServerSystemIcons.Find(system) is { } path
            ? (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), path)
            : null;

    /// <summary>Die Farbe dieses Zeichens.</summary>
    public static Brush SystemBrush(string? system)
        => ServerSystems.Find(system) is { } known ? new SolidColorBrush(ParseColor(known.Hex)) : Resource("PillSelectedBorderBrush");

    /// <summary>Der Name des Betriebssystems, für den Tooltip und Screenreader.</summary>
    public static string SystemName(string? system) => ServerSystems.Find(system)?.Name ?? string.Empty;

    // Was im Quadrat einer Kachel steht, entscheidet TileMarks.Face: das eigene Zeichen des Servers, sonst das Logo
    // seines Betriebssystems, sonst das Zeichen seiner Gruppe, sonst bei einem lokalen Terminal dessen Symbol, sonst
    // der Anfangsbuchstabe. Jedes Element des Quadrats fragt mit denselben Werten.

    /// <summary>Die Buchstaben im Quadrat; leer, wenn dort keine stehen.</summary>
    public static string TileText(string? mark, string? system, string? groupMark, string name, bool local)
        => TileMarks.Face(mark, system, groupMark, name, local) is (TileFace.Letters, var letters) ? letters : string.Empty;

    public static Visibility ShowsTileLetters(string? mark, string? system, string? groupMark, string name, bool local)
        => VisibleIf(TileMarks.Face(mark, system, groupMark, name, local).Kind == TileFace.Letters);

    public static Visibility ShowsTileSymbol(string? mark, string? system, string? groupMark, string name, bool local)
        => VisibleIf(TileMarks.Face(mark, system, groupMark, name, local).Kind == TileFace.Symbol);

    public static Visibility ShowsTileTerminal(string? mark, string? system, string? groupMark, string name, bool local)
        => VisibleIf(TileMarks.Face(mark, system, groupMark, name, local).Kind == TileFace.Terminal);

    public static Visibility ShowsTileSystem(string? mark, string? system, string? groupMark, string name, bool local)
        => VisibleIf(TileMarks.Face(mark, system, groupMark, name, local).Kind == TileFace.System);

    /// <summary>Das Symbol: das gewählte (eigene oder der Gruppe), sonst das gewöhnliche (Favorit: Stern).</summary>
    public static string TileGlyph(string? mark, string? system, string? groupMark, string name, bool local, bool favorite)
        => TileMarks.Face(mark, system, groupMark, name, local) is (TileFace.Symbol, var symbol) && TileMarks.Symbol(symbol) is { } chosen
            ? chosen.Glyph
            : ProfileGlyph(favorite);

    /// <summary>
    /// Das Logo des Betriebssystems in seiner Markenfarbe; im Kontrastdesign in der Farbe der Symbole – wie die übrigen
    /// Zeichen im Quadrat (<see cref="TileFore"/>).
    /// </summary>
    public static Brush TileSystemBrush(string? system) => IsHighContrast ? Resource("IconBrush") : SystemBrush(system);

    // Das Zeichen einer Gruppe steht klein vor ihrem Namen, in der Kennfarbe der Gruppe (ohne Farbe grau).

    public static Visibility ShowsGroupSymbol(string? mark) => VisibleIf(TileMarks.Symbol(mark) is not null);

    public static Visibility ShowsGroupLetters(string? mark) => VisibleIf(TileMarks.Letters(mark).Length > 0);

    public static string GroupMarkGlyph(string? mark) => TileMarks.Symbol(mark)?.Glyph ?? string.Empty;

    public static string GroupMarkLetters(string? mark) => TileMarks.Letters(mark);

    /// <summary>
    /// Schrift und Fläche des Quadrats in der Kennfarbe (<see cref="ProfileColors.Tile"/>); im Kontrastdesign die
    /// Farben des Designs – die Kacheln werden dann neu aufgebaut (ServerListView, <see cref="ContrastChanged"/>).
    /// </summary>
    public static Brush TileFore(string? color)
        => IsHighContrast ? Resource("IconBrush") : new SolidColorBrush(ParseColor(ProfileColors.Tile(color).Fore));

    public static Brush TileBack(string? color)
        => IsHighContrast ? Resource("FieldBackgroundBrush") : new SolidColorBrush(ParseColor(ProfileColors.Tile(color).Back));

    /// <summary>Die Zeile unter dem Namen: Benutzer@Host, bei einem lokalen Terminal „Dieser PC“.</summary>
    public static string TileSubtitle(bool local, string address) => local ? L.Get("TileThisPc") : address;

    /// <summary>Ob ein Profil eine Kennfarbe trägt.</summary>
    public static Visibility HasProfileColor(string? color) => VisibleIf(ProfileColors.Find(color).Hex is not null);

    /// <summary>Punkt auf der Kachel eines Servers: grün = antwortet, grau = antwortet nicht. Jede Kachel liest das beim Entstehen.</summary>
    public static Brush ReachBrush(string profileId) => StatusBrush(App.Shell.IsReachable(profileId) == true);

    /// <summary>Den Punkt gibt es nur für Server, die geprüft wurden.</summary>
    public static Visibility ReachVisibility(string profileId) => VisibleIf(App.Shell.IsReachable(profileId) is not null);

    /// <summary>Was der Punkt bedeutet – für Tooltip und Screenreader.</summary>
    public static string ReachText(string profileId) => App.Shell.IsReachable(profileId) switch
    {
        true => L.Get("Reachable"),
        false => L.Get("Unreachable"),
        null => string.Empty,
    };

    /// <summary>
    /// Die Punkte einer Verlaufskurve aus Werten zwischen 0 und 1, für eine Fläche von 100 × 24 (die Linie wird
    /// auf die Breite des Bereichs gestreckt). Der jüngste Wert steht rechts; bei wenigen Werten beginnt die
    /// Kurve rechts und wächst nach links.
    /// </summary>
    public static PointCollection Sparkline(IReadOnlyList<double>? values)
    {
        var points = new PointCollection();
        if (values is not { Count: > 0 })
        {
            return points;
        }

        const double Width = 100, Height = 24;
        var step = Width / (NexusSSH.Core.ViewModels.ServerInspectorViewModel.HistoryLength - 1);
        var start = Width - (step * (values.Count - 1));
        for (var index = 0; index < values.Count; index++)
        {
            points.Add(new Windows.Foundation.Point(start + (step * index), Height - (Math.Clamp(values[index], 0, 1) * (Height - 2)) - 1));
        }

        return points;
    }

    public static Visibility VisibleIfText(string? text) => VisibleIf(!string.IsNullOrEmpty(text));

    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Was die Zeile unten zeigt, solange es keine Werte gibt: warum nicht, sonst „misst …“.</summary>
    public static string MonitorBarWaiting(bool unavailable, string unavailableText)
        => unavailable ? unavailableText : L.Get("InspectorMeasuring");

    /// <summary>Sichtbar, wenn alle drei gelten – die Werte der Zeile unten: es gibt sie, sie misst, und es gibt Werte.</summary>
    public static Visibility VisibleIfAll(bool first, bool second, bool third) => VisibleIf(first && second && third);

    /// <summary>Sichtbar, wenn die ersten beiden gelten, das dritte nicht – „Messe …“, solange es noch keine Werte gibt.</summary>
    public static Visibility VisibleIfAllWithout(bool first, bool second, bool without) => VisibleIf(first && second && !without);

    /// <summary>Breite des gefüllten Teils eines Balkens der Zeile unten (40 breit, wie im Bild).</summary>
    public static double BarWidth(double percent) => double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) * 0.4 : 0;

    /// <summary>Eine Zahl als Text, für eine Bindung an eine Zahl.</summary>
    public static string Number(int value) => value.ToString(L.Culture);

    /// <summary>Name von „Transfers n“ für eine Sprachausgabe.</summary>
    public static string TransfersName(int count) => $"{L.Get("Transfers")} {count.ToString(L.Culture)}";

    /// <summary>Überschrift der Seite „Server-Details“.</summary>
    public static string DetailsTitle(string server) => L.Get("ServerDetailsTitle", server);

    /// <summary>Betriebssystem und Laufzeit in einer Zeile; was der Server nicht nennt, fällt weg.</summary>
    public static string SystemLine(string os, string uptime) => string.Join(" · ", new[] { os, uptime }.Where(part => part.Length > 0));

    /// <summary>Die erste Zeile eines Befehls – darunter in der Spalte der Snippets, gekürzt.</summary>
    public static string FirstLine(string text) => text.Split('\n', 2)[0].TrimEnd('\r');

    /// <summary>Der Stift einer Zeile in der Spalte der Snippets, nach dem Befehl benannt.</summary>
    public static string SnippetEditName(string name) => L.Get("SnippetEdit", name);

    /// <summary>Ohne verbundene Sitzung mit Shell sind die Einträge der Spalte grau.</summary>
    public static double SnippetsOpacity(bool runnable) => runnable ? 1 : 0.45;

    /// <summary>Eine Anzahl in der Schreibweise der Sprache der Oberfläche.</summary>
    public static string Count(int count) => count.ToString(L.Culture);

    /// <summary>Befehle in einem Ordner stehen eingerückt unter seiner Überschrift.</summary>
    public static Thickness SnippetIndent(string folder) => new(folder.Length > 0 ? 19 : 0, 0, 0, 0);

    /// <summary>Name von „Probleme n“ für eine Sprachausgabe.</summary>
    public static string ProblemsName(int count) => $"{L.Get("Problems")} {count.ToString(L.Culture)}";

    /// <summary>Das Gegenteil – für Bindungen, die es brauchen.</summary>
    public static bool Not(bool value) => !value;

    public static Visibility CollapsedIf(bool value) => VisibleIf(!value);

    /// <summary><c>#rrggbb</c> als Farbe; alles andere ergibt Schwarz.</summary>
    public static Color ParseColor(string hex)
        => hex.Length == 7 && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : Color.FromArgb(0xFF, 0, 0, 0);

    /// <summary>
    /// Hintergrund des Terminals nach dem gewählten Farbschema. Alle Flächen um das Terminal benutzen denselben
    /// Pinsel (<c>TerminalBackgroundBrush</c>) und folgen deshalb sofort.
    /// </summary>
    /// <remarks>
    /// Mit einem Kontrastdesign von Windows zeichnet die Terminal-Seite in dessen Farben (der Browser erzwingt
    /// sie); die Flächen darum nehmen dann die Fensterfarbe des Designs statt der des Farbschemas – sonst
    /// stünden Startseite und Rahmen schwarz zwischen hellen Karten.
    /// </remarks>
    public static void ApplyTerminalBackground(string hex)
    {
        if (Application.Current.Resources["TerminalBackgroundBrush"] is SolidColorBrush brush)
        {
            brush.Color = IsHighContrast && SystemColor(Windows.UI.ViewManagement.UIElementType.Window) is { } window
                ? window
                : ParseColor(hex);
        }
    }

    /// <summary>
    /// Farbe für alles, was die App selbst zeichnet: Verlaufskurven, Balken, die Marke einer Auswahl. Sonst die
    /// Akzentfarbe; mit einem Kontrastdesign dessen Farbe für Hervorgehobenes – der Akzentpinsel von WinUI ist dort
    /// die Farbe von Schrift *auf* Hervorgehobenem und hebt sich vom Fenster kaum ab (in „Wüste“ und „Nachthimmel“
    /// waren Kurven und Balken unsichtbar, gesehen 2026-10-13). Wie die Flächen gilt sie ab dem Start der App.
    /// </summary>
    public static Brush ChartBrush => Contrast().Chart;

    /// <summary>
    /// Rand um eine gezeichnete Fläche, deren Füllung sich im Kontrastdesign nicht vom Fenster abhebt (der leere
    /// Teil eines Balkens): dort die Schriftfarbe, sonst nichts.
    /// </summary>
    public static Brush ChartFrameBrush => Contrast().Frame;

    // Je ein Pinsel für alle, die ihn benutzen: Wechselt Windows das Design, während die App läuft, ändert sich
    // seine Farbe (ApplyContrast) und mit ihr alles Gezeichnete. Angelegt beim ersten Gebrauch – im Thread der
    // Oberfläche.
    private static (SolidColorBrush Chart, SolidColorBrush Frame)? _contrast;

    /// <summary>Die Farben wurden neu eingestellt – für Ansichten, die im Code einen Pinsel des Designs gesetzt haben.</summary>
    public static event Action? ContrastChanged;

    private static (SolidColorBrush Chart, SolidColorBrush Frame) Contrast()
    {
        if (_contrast is null)
        {
            _contrast = (new SolidColorBrush(), new SolidColorBrush());
            ApplyContrast();
        }

        return _contrast.Value;
    }

    private static (bool Contrast, Color? Window)? _seenDesign;

    /// <summary>
    /// Ob Windows seit dem letzten Nachsehen ein Kontrastdesign ein-, aus- oder umgeschaltet hat. Nachgesehen wird
    /// in Abständen: Die Meldungen von Windows dafür kamen in der App nicht an (in der Sandbox gesehen,
    /// 2026-10-13), und die Frage kostet nichts.
    /// </summary>
    public static bool DesignChanged()
    {
        var now = (IsHighContrast, SystemColor(Windows.UI.ViewManagement.UIElementType.Window));
        var changed = _seenDesign is { } seen && seen != now;
        _seenDesign = now;
        return changed;
    }

    /// <summary>
    /// Stellt die Farben dessen, was die App selbst zeichnet, auf das ein, was in Windows gerade gilt. Beim Start
    /// von selbst; noch einmal, wenn ein Kontrastdesign ein- oder ausgeschaltet wird.
    /// </summary>
    public static void ApplyContrast()
    {
        var (chart, frame) = Contrast();
        var resources = Application.Current.Resources;
        var contrast = IsHighContrast;
        chart.Color = contrast && SystemColor(Windows.UI.ViewManagement.UIElementType.Highlight) is { } highlight ? highlight
            : resources["AccentFillColorDefaultBrush"] is SolidColorBrush accent ? accent.Color
            : Microsoft.UI.Colors.DodgerBlue;
        frame.Color = contrast && SystemColor(Windows.UI.ViewManagement.UIElementType.WindowText) is { } text ? text : Microsoft.UI.Colors.Transparent;
        ContrastChanged?.Invoke();
    }

    // Eine Farbe des Designs von Windows, wie sie jetzt gilt – gefragt wird Windows selbst: Die Ressourcen der App
    // nennen nach einem Wechsel des Designs im Code noch eine Weile die alten.
    private static Color? SystemColor(Windows.UI.ViewManagement.UIElementType type)
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().UIElementColor(type);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>In Windows ist ein Kontrastdesign eingeschaltet.</summary>
    public static bool IsHighContrast
    {
        get
        {
            try
            {
                return new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Setzt die Akzentfarbe der Oberfläche samt ihren helleren und dunkleren Stufen, aus denen WinUI seine Pinsel
    /// ableitet. Vor dem ersten Fenster zu rufen; <c>null</c> lässt die Farbe von Windows stehen.
    /// </summary>
    public static void ApplyAccent(string? hex)
    {
        if (hex is null)
        {
            return;
        }

        var color = ParseColor(hex);
        var resources = Application.Current.Resources;
        resources["SystemAccentColor"] = color;
        resources["SystemAccentColorLight1"] = Mix(color, 0xFF, 0.2);
        resources["SystemAccentColorLight2"] = Mix(color, 0xFF, 0.4);
        resources["SystemAccentColorLight3"] = Mix(color, 0xFF, 0.6);
        resources["SystemAccentColorDark1"] = Mix(color, 0x00, 0.2);
        resources["SystemAccentColorDark2"] = Mix(color, 0x00, 0.4);
        resources["SystemAccentColorDark3"] = Mix(color, 0x00, 0.6);

        // Der Akzent der eigenen Flächen (eingeschaltete Knöpfe, Balken, Kästchen) ist die Farbe selbst, nicht eine
        // ihrer hellen Fassungen, die WinUI für Schrift nimmt – so wie im Entwurf.
        if (resources.ThemeDictionaries.TryGetValue("Default", out var theme) && theme is ResourceDictionary normal
            && normal.TryGetValue("DraftAccentBrush", out var accent) && accent is SolidColorBrush brush)
        {
            brush.Color = color;
        }

        static Color Mix(Color color, byte toward, double share)
        {
            byte Channel(byte value) => (byte)Math.Round(value + ((toward - value) * share));
            return Color.FromArgb(0xFF, Channel(color.R), Channel(color.G), Channel(color.B));
        }
    }

    /// <summary>Abstand zwischen zwei Karten – und damit die Breite der Trenner darin. Kleiner in der kompakten Darstellung.</summary>
    public static double Gap { get; private set; } = 8;

    /// <summary>Rundung einer Karte; kleiner in der kompakten Darstellung.</summary>
    public static double CardRadius { get; private set; } = 10;

    /// <summary>
    /// Stellt auf die kompakte Darstellung um: dichtere Listenzeilen, kleinere Rundungen, schmalere Lücken. Vor dem
    /// ersten Fenster zu rufen – die Ansichten holen sich die Stile beim Laden, und die werden hier ersetzt.
    /// </summary>
    public static void ApplyDensity(bool compact)
    {
        if (!compact)
        {
            return;
        }

        (Gap, CardRadius) = (4, 6);
        var resources = Application.Current.Resources;

        var card = new Style(typeof(Grid));
        card.Setters.Add(new Setter(Panel.BackgroundProperty, resources["CardBackgroundBrush"]));
        card.Setters.Add(new Setter(Grid.BorderBrushProperty, resources["CardBorderBrush"]));
        card.Setters.Add(new Setter(Grid.BorderThicknessProperty, new Thickness(1)));
        card.Setters.Add(new Setter(Grid.CornerRadiusProperty, new CornerRadius(CardRadius)));
        resources["CardStyle"] = card;

        var row = new Style(typeof(ListViewItem)) { BasedOn = (Style)resources["DefaultListViewItemStyle"] };
        row.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 24d));
        row.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 0, 12, 0)));
        row.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        resources["CompactListItemStyle"] = row;
    }

    /// <summary>
    /// Lässt ein Element beim Einblenden kurz aufscheinen statt hart zu erscheinen – oder nimmt das wieder weg.
    /// Greift bei jedem Wechsel von <c>Collapsed</c> nach <c>Visible</c>.
    /// </summary>
    public static void FadeIn(UIElement element, bool enabled)
    {
        if (!enabled)
        {
            ElementCompositionPreview.SetImplicitShowAnimation(element, null);
            return;
        }

        var compositor = ElementCompositionPreview.GetElementVisual(element).Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(160);
        ElementCompositionPreview.SetImplicitShowAnimation(element, fade);
    }

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}
