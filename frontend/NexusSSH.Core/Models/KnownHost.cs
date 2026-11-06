namespace NexusSSH.Core.Models;

/// <summary>
/// Ein Server, dessen Host-Schlüssel die App sich gemerkt hat. <see cref="Name"/> ist der Eintrag, wie er in der
/// Datei steht (<c>host</c> oder <c>[host]:port</c>) – mit ihm wird er auch wieder vergessen. Oder, mit
/// <see cref="IsAuthority"/>, eine Zertifizierungsstelle: <see cref="Name"/> sind dann die Muster der Server, deren
/// Zertifikate sie beglaubigt.
/// </summary>
public sealed record KnownHost(string Name, string Algorithm, string Fingerprint, bool IsAuthority = false)
{
    /// <summary>Server und Art des Schlüssels – Listen lesen das Screenreadern als Bezeichnung der Zeile vor.</summary>
    public override string ToString() => $"{Name}, {Algorithm}";
}
