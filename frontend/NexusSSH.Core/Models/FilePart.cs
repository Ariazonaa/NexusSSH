namespace NexusSSH.Core.Models;

/// <summary>Ein Stück einer Datei des Servers und die Größe der ganzen Datei.</summary>
public sealed record FilePart(byte[] Data, long TotalSize)
{
    /// <summary>Mehr liefert ein Backend nie auf einmal.</summary>
    public const int MaxLength = 1024 * 1024;
}
