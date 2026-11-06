namespace NexusSSH.Core.Models;

/// <summary>
/// Etwas, das ein Download anlegen würde: eine Datei oder ein Verzeichnis des Servers samt dem Ort, an dem es auf
/// diesem Rechner landet.
/// </summary>
/// <param name="RemotePath">Voller Pfad auf dem Server.</param>
/// <param name="LocalPath">
/// Relativ zum Zielordner, mit <c>/</c> getrennt. Die Namen legt das Backend fest – sie müssen nicht die des Servers
/// sein (<c>a:b</c> wird unter Windows zu <c>a_b</c>).
/// </param>
/// <param name="Modified"><see cref="DateTimeOffset.MinValue"/>, wenn der Server keine Zeit nennt.</param>
public sealed record RemoteTreeEntry(string RemotePath, string LocalPath, bool IsDirectory, long Size, DateTimeOffset Modified);
