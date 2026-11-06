using NexusSSH.Core.Services;

namespace NexusSSH.Core.Models;

/// <summary>
/// Eine Shell dieses Rechners, die ein lokales Terminal öffnen kann – so nennt der Kern sie (<c>local.shells</c>).
/// <see cref="Id"/> steht im Profil als <see cref="ServerProfile.Host"/>, <see cref="Name"/> ist sein englischer
/// Anzeigename; Programm und Argumente kennt nur der Kern.
/// </summary>
public sealed record LocalShell(string Id, string Name)
{
    /// <summary>Der Name in der Sprache der Oberfläche (<see cref="LocalShellNames.Of"/>) – Listen lesen ihn vor.</summary>
    public string DisplayName => LocalShellNames.Of(Id, Name);

    public override string ToString() => DisplayName;
}
