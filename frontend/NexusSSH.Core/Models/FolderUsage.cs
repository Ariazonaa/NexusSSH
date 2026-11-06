namespace NexusSSH.Core.Models;

/// <summary>Wie viel Platz ein Eintrag auf dem Server belegt – ein Verzeichnis samt allem darin (<c>du</c>).</summary>
public sealed record FolderUsage(string Path, long Bytes);
