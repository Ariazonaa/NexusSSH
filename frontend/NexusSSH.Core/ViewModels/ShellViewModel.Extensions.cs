using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Extensions;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.ViewModels;

// Erweiterungspakete (.nxext): Farbschemata, gespeicherte Befehle, Regeln zum Hervorheben und Tabellen zum Ergänzen
// aus Dateien anderer. Sie liegen über den eigenen Inhalten, nie darin – Entfernen nimmt alles wieder heraus. Ein
// Befehl aus einem Paket läuft erst nach Freigabe (je Text, „Immer erlauben“ nur für genau diesen Text).
public sealed partial class ShellViewModel
{
    /// <summary>So viele Regeln zum Hervorheben aus allen Paketen zusammen; so viele Tabellen zum Ergänzen.</summary>
    private const int MaxPackHighlights = 40;
    private const int MaxPackCompletions = 40;

    private ExtensionStore? _extensions;

    /// <summary>Die installierten Pakete, an und aus.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ExtensionPackage> Extensions { get; private set; } = BindableList.Of<ExtensionPackage>([]);

    public bool HasNoExtensions => Extensions.Count == 0;

    private IEnumerable<ExtensionPackage> ActivePacks => Extensions.Where(package => !package.IsDisabled);

    /// <summary>Die Farbschemata der eingeschalteten Pakete.</summary>
    private IReadOnlyList<TerminalTheme> PackThemes => [.. ActivePacks.SelectMany(package => package.Themes)];

    /// <summary>Die Tabellen zum Ergänzen aus den Paketen – eine eingebaute desselben Programms geht vor.</summary>
    public IReadOnlyList<CommandTable> PackCompletions
        => [.. ActivePacks.SelectMany(package => package.Completions)
            .Where(table => Completions.Tables.All(known => known.Command != table.Command))
            .DistinctBy(table => table.Command)
            .Take(MaxPackCompletions)];

    private IEnumerable<HighlightRule> PackHighlights => ActivePacks.SelectMany(package => package.Highlights).Take(MaxPackHighlights);

    // Die Befehle der eingeschalteten Pakete, je Paket in einem Ordner mit seinem Namen; nur lesbar.
    private IEnumerable<Snippet> PackSnippets => ActivePacks.SelectMany(package => package.Snippets.Select(snippet => snippet with
    {
        Folder = SnippetFolders.Clean(snippet.Folder.Length > 0 ? $"{package.Name} / {snippet.Folder}" : package.Name),
        Pack = package.Id,
    }));

    /// <summary>Liest die Pakete aus <paramref name="store"/> und nimmt sie dazu.</summary>
    public void UseExtensions(ExtensionStore store)
    {
        _extensions = store;
        ReloadExtensions();
    }

    /// <summary>Liest eine Datei als Paket – für die Vorschau vor dem Installieren. <c>null</c>, wenn es keines ist.</summary>
    public static ExtensionPackage? ReadExtension(string? text, out string? problem) => ExtensionFormat.Parse(text, out problem);

    /// <summary>Das installierte Paket mit dieser Kennung (für „ersetzt Version …“ in der Vorschau).</summary>
    public ExtensionPackage? InstalledExtension(string id) => Extensions.FirstOrDefault(package => package.Id == id);

    /// <summary>Installiert ein geprüftes Paket (ersetzt eines mit derselben Kennung). <c>false</c>, wenn es nicht ging.</summary>
    public bool InstallExtension(ExtensionPackage package)
    {
        if (_extensions is null || (InstalledExtension(package.Id) is null && Extensions.Count >= ExtensionStore.MaxPackages))
        {
            return false;
        }

        try
        {
            _extensions.Install(package);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log(L.Get("LogExtensionNotInstalled", package.Name, e.Message), LogSeverity.Error);
            return false;
        }

        Log(L.Get("LogExtensionInstalled", package.Name, package.Version));
        ReloadExtensions();
        return true;
    }

    public void RemoveExtension(ExtensionPackage package)
    {
        _extensions?.Remove(package.Id);
        Log(L.Get("LogExtensionRemoved", package.Name));
        ReloadExtensions();
    }

    public void SetExtensionEnabled(ExtensionPackage package, bool enabled)
    {
        _extensions?.SetDisabled(package.Id, !enabled);
        ReloadExtensions();
    }

    /// <summary>
    /// Ein Paket aus eigenen Inhalten, zum Weitergeben: die eigenen Farbschemata, die eigenen Befehle, die eigenen
    /// Regeln zum Hervorheben – was davon gewählt ist. <c>null</c>, wenn nichts darin wäre.
    /// </summary>
    public ExtensionPackage? OwnPackage(string name, string author, bool themes, bool snippets, bool highlights)
    {
        var package = new ExtensionPackage(
            ExtensionFormat.IdOf(name),
            name,
            "1.0.0",
            author,
            string.Empty,
            themes ? Settings.CustomThemes : [],
            snippets ? Settings.Snippets : [],
            highlights ? TerminalHighlights.Parse(Settings.Highlights) : [],
            []);

        // Was wieder eingelesen würde – so steht in der Datei nur, was ein anderer auch bekommt.
        return ExtensionFormat.Parse(ExtensionFormat.Write(package), out _);
    }

    /// <summary>Darf dieser Befehl aus einem Paket ohne Rückfrage laufen? (Ein eigener immer.)</summary>
    private bool IsApproved(Snippet snippet) => !snippet.IsFromPack || _extensions?.IsApproved(snippet.Pack, snippet.Text) == true;

    // Ein Befehl aus einem Paket, der noch nicht freigegeben ist: erst fragen, dann tippen.
    private async Task RunPackSnippetAsync(SessionViewModel session, Snippet snippet)
    {
        var package = InstalledExtension(snippet.Pack);
        var answer = await _prompts.ConfirmPackCommandAsync(package?.Name ?? snippet.Pack, snippet.Name, snippet.Text);
        if (answer == CommandAnswer.Decline || !ReferenceEquals(ActiveSession, session) || session.State != ConnectionState.Connected)
        {
            return;
        }

        if (answer == CommandAnswer.Always)
        {
            _extensions?.Approve(snippet.Pack, snippet.Text);
        }

        SendSnippet(session, snippet);
    }

    private void ReloadExtensions()
    {
        Extensions = BindableList.Of(_extensions?.Load() ?? []);
        OnPropertyChanged(nameof(HasNoExtensions));

        // Ein Schema aus einem Paket, das beim Start gewählt war, gibt es erst jetzt; eines, dessen Paket weg ist, nicht mehr.
        var wanted = Settings.TerminalTheme;
        ThemesChanged();
        if (Themes.FirstOrDefault(theme => theme.Id == wanted) is { } chosen)
        {
            TerminalTheme = chosen;
        }
        else if (TerminalThemes.IsFromPack(TerminalTheme.Id))
        {
            TerminalTheme = TerminalThemes.Find(TerminalThemes.DefaultId);
        }

        FilterSnippets();
        OnPropertyChanged(nameof(Snippets));
        OnPropertyChanged(nameof(HighlightRules));
        OnPropertyChanged(nameof(Highlights));
    }
}
