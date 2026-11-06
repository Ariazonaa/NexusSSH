using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

/// <summary>
/// Eine Zeile der Spalte mit den Snippets: die Überschrift eines Ordners (<see cref="Snippet"/> ist dann
/// <c>null</c>) oder ein Befehl darin.
/// </summary>
/// <param name="Folder">Der Ordner – bei einer Überschrift ihr Name, bei einem Befehl seiner (leer: ohne).</param>
/// <param name="Count">Bei einer Überschrift: wie viele Befehle der Ordner gerade zeigt bzw. eingeklappt hält.</param>
public sealed record SnippetItem(Snippet? Snippet, string Folder, bool IsCollapsed = false, int Count = 0)
{
    public bool IsHeading => Snippet is null;

    public bool IsCommand => Snippet is not null;

    /// <summary>Was die Zeile groß zeigt: der Name des Ordners oder des Befehls.</summary>
    public string Name => Snippet?.Name ?? Folder;

    /// <summary>Der Befehl selbst; bei einer Überschrift leer.</summary>
    public string Text => Snippet?.Text ?? string.Empty;

    /// <summary>Die Liste liest das Screenreadern vor – bei einer Überschrift samt Zahl und Zustand.</summary>
    public override string ToString() => Snippet is { } snippet
        ? snippet.Name
        : L.Get(IsCollapsed ? "SnippetFolderClosed" : "SnippetFolderOpen", Folder, Count);
}
