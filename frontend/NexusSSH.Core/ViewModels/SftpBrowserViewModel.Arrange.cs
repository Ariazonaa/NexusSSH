using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

/// <summary>
/// Ordnen auf dem Server selbst: kopieren, verschieben, duplizieren, mehrere umbenennen – und die letzte
/// Umbenennung oder Verschiebung zurücknehmen. Kopiert wird mit <c>cp</c> auf dem Server (ohne den Umweg über
/// diesen Rechner), verschoben mit dem Umbenennen von SFTP und, wo das nicht reicht, mit <c>mv</c>.
/// </summary>
public sealed partial class SftpBrowserViewModel
{
    private IReadOnlyList<string> _held = [];
    private IReadOnlyList<(string From, string To)> _undo = [];

    /// <summary>Was zum Einfügen bereitliegt: die Pfade und ob sie ausgeschnitten sind. Gilt für diese Sitzung.</summary>
    public IReadOnlyList<string> Held => _held;

    [ObservableProperty]
    public partial bool HeldIsCut { get; private set; }

    [ObservableProperty]
    public partial bool CanPaste { get; private set; }

    /// <summary>Ob sich die letzte Umbenennung oder Verschiebung zurücknehmen lässt.</summary>
    [ObservableProperty]
    public partial bool CanUndo { get; private set; }

    /// <summary>Merkt Einträge der Liste zum Einfügen vor – zum Kopieren oder, ausgeschnitten, zum Verschieben.</summary>
    public void Hold(IEnumerable<RemoteEntry> entries, bool cut)
    {
        _held = [.. entries.Select(PathOf)];
        HeldIsCut = cut;
        CanPaste = _held.Count > 0;
    }

    /// <summary>Die Namen des Vorgemerkten, die es im angezeigten Verzeichnis schon gibt – danach fragt die Oberfläche.</summary>
    public IReadOnlyList<string> PasteConflicts()
        => SameFolder() ? [] : ExistingNames(_held.Select(RemotePath.Name));

    /// <summary>
    /// Fügt das Vorgemerkte ins angezeigte Verzeichnis ein. Im selben Verzeichnis entsteht beim Kopieren eine
    /// Kopie mit eigenem Namen; Verschieben an Ort und Stelle tut nichts. Was am Ziel schon liegt, wird nur mit
    /// <paramref name="replace"/> ersetzt – sonst bleibt es, und der Eintrag wird ausgelassen.
    /// </summary>
    public async Task<bool> PasteAsync(bool replace = false)
    {
        if (_held.Count == 0 || (HeldIsCut && SameFolder()))
        {
            return false;
        }

        var taken = _all.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        var pairs = new List<(string From, string To)>();
        foreach (var source in _held)
        {
            var name = RemotePath.Name(source);
            if (SameFolder())
            {
                name = RenamePlan.CopyName(name, taken);
                taken.Add(name);
            }
            else if (taken.Contains(name) && !replace)
            {
                continue;
            }

            pairs.Add((source, RemotePath.Combine(CurrentPath, name)));
        }

        var moving = HeldIsCut;
        var done = pairs.Count == 0 || await ChangeAsync(files => moving ? MoveAsync(files, pairs, replace) : files.CopyAsync(pairs));
        if (done && moving)
        {
            Remember(pairs);
            Hold([], cut: false);
        }

        return done;
    }

    /// <summary>Legt neben jedem Eintrag eine Kopie mit eigenem Namen an („name (2).txt“).</summary>
    public Task<bool> DuplicateAsync(IReadOnlyList<RemoteEntry> entries)
    {
        var taken = _all.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        var pairs = new List<(string From, string To)>();
        foreach (var entry in entries)
        {
            var name = RenamePlan.CopyName(entry.Name, taken);
            taken.Add(name);
            pairs.Add((PathOf(entry), RemotePath.Combine(CurrentPath, name)));
        }

        return ChangeAsync(files => files.CopyAsync(pairs));
    }

    /// <summary>
    /// Benennt mehrere Einträge um, wie <see cref="RenamePlan"/> es errechnet hat. Tauschen Einträge ihre Namen
    /// (a→b, b→a), geht es über Zwischennamen – sonst stünde der zweite dem ersten im Weg.
    /// </summary>
    public async Task<bool> RenameManyAsync(IReadOnlyList<(string From, string To)> names)
    {
        var changes = names.Where(pair => pair.From != pair.To).ToList();
        if (changes.Count == 0)
        {
            return true;
        }

        var sources = changes.Select(pair => pair.From).ToHashSet(StringComparer.Ordinal);
        var steps = new List<(string From, string To)>();
        if (changes.Exists(pair => sources.Contains(pair.To)))
        {
            var stamp = Guid.NewGuid().ToString("N")[..8];
            var parked = changes.Select((pair, index) => (pair.From, Park: $".nexus-rename-{stamp}-{index}", pair.To)).ToList();
            steps.AddRange(parked.Select(step => (step.From, step.Park)));
            steps.AddRange(parked.Select(step => (step.Park, step.To)));
        }
        else
        {
            steps.AddRange(changes);
        }

        var pairs = steps.Select(step => (RemotePath.Combine(CurrentPath, step.From), RemotePath.Combine(CurrentPath, step.To))).ToList();
        var completed = new List<(string From, string To)>();
        var done = await ChangeAsync(async files =>
        {
            foreach (var pair in pairs)
            {
                await files.RenameAsync(pair.Item1, pair.Item2);
                completed.Add(pair);
            }
        });

        // Auch nach einem Fehler mittendrin: Was schon umbenannt ist, lässt sich zurücknehmen.
        Remember(completed);
        return done;
    }

    /// <summary>Nimmt die letzte Umbenennung oder Verschiebung zurück – in umgekehrter Reihenfolge.</summary>
    public async Task<bool> UndoAsync()
    {
        var back = _undo.Reverse().Select(pair => (From: pair.To, To: pair.From)).ToList();
        if (back.Count == 0)
        {
            return false;
        }

        Remember([]);
        return await ChangeAsync(files => MoveAsync(files, back, replace: false));
    }

    /// <summary>
    /// Ob sich auf dem Server Befehle ausführen lassen. Über FTP nicht, und nicht, wenn der Server nach dem
    /// Verbinden sagt, dass er keine annimmt (Konto ohne Befehle, OpenSSH unter Windows mit <c>cmd.exe</c>) – was
    /// Befehle braucht (Packen, Prüfsumme, Belegung, Mitlesen, als root), zeigt die Ansicht dann gar nicht erst.
    /// Kopieren gehört nicht dazu: Ohne Befehle kopiert der Kern über die Dateiverbindung.
    /// </summary>
    public bool HasCommands
    {
        get => _hasCommands;
        set => SetProperty(ref _hasCommands, value);
    }

    private bool _hasCommands = true;

    /// <summary>Das Profil der Sitzung – daraus entstehen die Links in „Pfad kopieren als …“.</summary>
    public ServerProfile? Profile { get; init; }

    /// <summary>
    /// Ein Server mit OpenSSH unter Windows: Befehle gehen dort als PowerShell, aber nur ein Teil – Prüfsumme,
    /// Belegung, Packen und Kopieren ja, Mitlesen und „als root“ nicht (beides braucht ein POSIX-System).
    /// </summary>
    [ObservableProperty]
    public partial bool IsWindowsServer { get; set; }

    /// <summary>Der Dateibrowser arbeitet gerade als root (<c>sudo</c>) – alles, was er tut, geschieht mit dessen Rechten.</summary>
    [ObservableProperty]
    public partial bool IsRoot { get; private set; }

    /// <summary>
    /// Stellt den Dateibrowser auf root um. <c>null</c>: <c>sudo</c> verlangt ein Passwort (dann mit einem noch
    /// einmal rufen); sonst, ob es gelang – der Grund eines Scheiterns steht in <see cref="Error"/>.
    /// </summary>
    public async Task<bool?> ElevateAsync(string? password = null)
    {
        if (_files is not { } files)
        {
            return false;
        }

        try
        {
            await files.ElevateAsync(password);
        }
        catch (RemoteFileException e) when (e.Code == "sudo_password" && password is null)
        {
            return null;
        }
        catch (RemoteFileException e)
        {
            Error = Describe(e);
            return false;
        }

        await ShowAsync(CurrentPath);
        IsRoot = files.IsElevated;
        return true;
    }

    /// <summary>Zurück zum eigenen Konto: ein neuer Kanal mit den Rechten des Benutzers.</summary>
    public async Task LeaveRootAsync()
    {
        if (_files is not { } files)
        {
            return;
        }

        try
        {
            await files.ReopenAsync();
        }
        catch (RemoteFileException e)
        {
            Error = Describe(e);
        }

        IsRoot = files.IsElevated;

        // Was root zeigen durfte, darf der Benutzer womöglich nicht sehen – dann geht es zurück nach Hause.
        if (!await ShowAsync(CurrentPath))
        {
            await ShowAsync(HomePath);
        }
    }

    private bool SameFolder() => _held.Count > 0 && _held.All(path => RemotePath.Parent(path) == CurrentPath);

    private void Remember(IReadOnlyList<(string From, string To)> pairs)
    {
        _undo = pairs;
        CanUndo = pairs.Count > 0;
    }

    // Umbenennen kann jedes Konto, auch eines ohne Shell – es scheitert aber über Dateisysteme hinweg und, je nach
    // Server, wenn am Ziel schon etwas liegt. Dann (und gleich, wenn ersetzt werden soll) nimmt der Kern `mv`.
    private static async Task MoveAsync(IRemoteFileSystem files, IReadOnlyList<(string From, string To)> pairs, bool replace)
    {
        if (replace)
        {
            await files.CopyAsync(pairs, move: true);
            return;
        }

        for (var index = 0; index < pairs.Count; index++)
        {
            try
            {
                await files.RenameAsync(pairs[index].From, pairs[index].To);
            }
            catch (RemoteFileException e) when (e.Code is not ("not_found" or "exists"))
            {
                await files.CopyAsync([.. pairs.Skip(index)], move: true);
                return;
            }
        }
    }
}
