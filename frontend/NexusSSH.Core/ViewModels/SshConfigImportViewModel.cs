using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Exchange;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.ViewModels;

/// <summary>
/// Ein Server in der Importliste: aus <c>~/.ssh/config</c> oder – mit <see cref="Foreign"/> – eine gespeicherte
/// Sitzung eines anderen Programms.
/// </summary>
public sealed partial class ImportCandidate(SshConfigHost host, ForeignSession? foreign = null) : ObservableObject
{
    public SshConfigHost Host { get; } = host;

    public ForeignSession? Foreign { get; } = foreign;

    /// <summary>Was übernommen wird, in einer Zeile: Herkunft, Adresse, Sprung-Server, Schlüsseldatei.</summary>
    public string Summary { get; } = string.Join("  ·  ", new[]
    {
        foreign?.Source,
        host.Address,
        foreign is { Group.Length: > 0 } ? foreign.Group : null,
        host.Jump is null ? null : L.Get("ImportVia", host.Jump),
        host.KeyPath is null ? null : Path.GetFileName(host.KeyPath),
        host.Tunnels > 0 ? L.Get("ImportTunnels", host.Tunnels) : null,
        host.Exists ? L.Get("ImportExists") : null,
    }.Where(part => part is not null));

    /// <summary>Was für den Server in der Datei steht und nicht übernommen wird; leer, wenn es nichts gibt.</summary>
    public string Skipped { get; } = host.Skipped is { Count: > 0 } skipped ? L.Get("ImportSkipped", string.Join(", ", skipped)) : string.Empty;

    /// <summary>Vorgewählt ist, was es noch nicht als Profil gibt.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; } = !host.Exists;

    public override string ToString() => $"{Host.Alias}, {Summary}";
}

/// <summary>
/// Auswahl der Server, die als Profile übernommen werden: aus <c>~/.ssh/config</c> (Spec §8) oder aus den
/// gespeicherten Sitzungen von PuTTY, WinSCP und MobaXterm.
/// </summary>
public sealed partial class SshConfigImportViewModel : ObservableObject
{
    public SshConfigImportViewModel(SshConfigListing listing)
        : this(
            listing.Hosts.Select(host => new ImportCandidate(host)),
            L.Get("ImportTitle"),
            listing.Hosts.Count > 0 ? L.Get("ImportIntro", listing.Path ?? "~/.ssh/config") : L.Get("ImportEmpty", listing.Path ?? "~/.ssh/config"),
            L.Get("ImportHint"))
    {
        CanLink = listing.Hosts.Count > 0;
        Unchecked = listing.Unchecked is { Count: > 0 } conditions ? L.Get("ImportUnchecked", string.Join(", ", conditions)) : string.Empty;
    }

    /// <summary>Welche Bedingungen hinter <c>Match</c> die App nicht prüft – ein Satz für unter der Liste; sonst leer.</summary>
    public string Unchecked { get; } = string.Empty;

    /// <summary>Ob sich die Auswahl mit ihrer Datei verknüpfen lässt – nur bei <c>~/.ssh/config</c>, nicht bei fremden Sitzungen.</summary>
    public bool CanLink { get; }

    /// <summary>
    /// Die gewählten Profile bleiben mit ihrem Eintrag in der Datei verknüpft: Adresse, Port, Benutzer und Schlüssel
    /// werden dann bei jedem Start neu daraus gelesen.
    /// </summary>
    [ObservableProperty]
    public partial bool Linked { get; set; }

    /// <summary>Die Sitzungen anderer Programme; was es als Profil schon gibt, ist nicht vorgewählt.</summary>
    public SshConfigImportViewModel(IReadOnlyList<ForeignSession> sessions, IReadOnlyList<ServerProfile> profiles)
        : this(
            sessions.Select(session => new ImportCandidate(
                new SshConfigHost(session.Name, session.Host, session.Port, session.User, session.KeyPath, Jump: null, ForwardAgent: false, ForeignSessions.Exists(session, profiles)),
                session)),
            L.Get("SessionsImportTitle"),
            sessions.Count > 0 ? L.Get("SessionsImportIntro") : L.Get("SessionsImportEmpty"),
            L.Get("SessionsImportHint"))
    {
    }

    private SshConfigImportViewModel(IEnumerable<ImportCandidate> candidates, string title, string intro, string hint)
    {
        Candidates = BindableList.Of(candidates);
        (Title, Intro, Hint) = (title, intro, hint);
        foreach (var candidate in Candidates)
        {
            candidate.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanImport));
        }
    }

    public IReadOnlyList<ImportCandidate> Candidates { get; }

    public string Title { get; }

    public string Intro { get; }

    public string Hint { get; }

    public bool HasCandidates => Candidates.Count > 0;

    public bool CanImport => Candidates.Any(candidate => candidate.IsSelected);

    public IReadOnlyList<string> SelectedAliases => [.. Candidates.Where(candidate => candidate.IsSelected).Select(candidate => candidate.Host.Alias)];

    public IReadOnlyList<ForeignSession> SelectedSessions => [.. Candidates.Where(candidate => candidate.IsSelected).Select(candidate => candidate.Foreign).OfType<ForeignSession>()];
}
