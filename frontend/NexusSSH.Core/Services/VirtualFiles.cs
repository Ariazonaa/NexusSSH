namespace NexusSSH.Core.Services;

/// <summary>
/// Ein Eintrag, den ein anderes Programm als Datei oder Ordner bekommt, ohne dass es ihn hier schon gibt.
/// </summary>
/// <param name="Path">Wo er beim Empfänger landet, relativ zu dessen Ziel und mit <c>/</c> getrennt (<c>www/css/a.css</c>).</param>
/// <param name="Modified"><see cref="DateTimeOffset.MinValue"/>, wenn unbekannt.</param>
public sealed record VirtualEntry(string Path, bool IsDirectory, long Size, DateTimeOffset Modified);

/// <summary>
/// Dateien und Ordner, die einem anderen Programm angeboten werden und erst entstehen, wenn es sie liest – die
/// Seite, die ohne Windows auskommt. Was Windows dazu braucht (ein OLE-Datenobjekt), liegt in der App.
/// Beliebiger Thread: Der Empfänger fragt aus seinen eigenen Threads, auch mehrmals zugleich.
/// </summary>
public interface IVirtualFiles
{
    /// <summary>
    /// Alles, was angeboten wird – jeder Ordner vor seinem Inhalt. <c>null</c>, wenn es sich nicht ermitteln ließ;
    /// dann bekommt der Empfänger nichts.
    /// </summary>
    Task<IReadOnlyList<VirtualEntry>?> EntriesAsync();

    /// <summary>
    /// Der Inhalt des Eintrags mit dieser Nummer aus <see cref="EntriesAsync"/>: ein Strom, der von vorn nach
    /// hinten gelesen wird. Lesen darf warten, bis der Inhalt da ist, und scheitert, wenn er nicht vollständig
    /// kommt: mit <see cref="OperationCanceledException"/>, wenn jemand abgebrochen hat, sonst mit
    /// <see cref="IOException"/>. <c>null</c>, wenn es nichts zu lesen gibt. Der Aufrufer schließt den Strom.
    /// </summary>
    Task<Stream?> OpenAsync(int index);

    /// <summary>Der Empfänger ist fertig – mit Erfolg oder ohne. Was nur für ihn geholt wurde, kann weg.</summary>
    void Completed(bool succeeded);
}
