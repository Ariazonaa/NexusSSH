namespace NexusSSH.Core.Models;

/// <summary>Das Format einer Schlüsseldatei.</summary>
public enum KeyFormat
{
    /// <summary>Das Format von OpenSSH.</summary>
    OpenSsh,

    /// <summary>Das Format von PuTTY (<c>.ppk</c>).</summary>
    Ppk,

    /// <summary>Die älteren PEM-Formate – gelesen, nie geschrieben.</summary>
    Pem,
}

/// <summary>
/// Eine eigene Schlüsseldatei, wie die Seite „Schlüssel“ sie zeigt. Alles darin steht im Kopf der Datei oder in
/// <c>….pub</c> daneben – gelesen ohne Passphrase. <see cref="Algorithm"/>, <see cref="Fingerprint"/> und
/// <see cref="PublicKey"/> sind leer, wenn die Datei sie ohne Passphrase nicht verrät (verschlüsseltes PEM ohne
/// Datei daneben).
/// </summary>
/// <param name="Algorithm">Art des Schlüssels, wie SSH sie nennt, z. B. <c>ssh-ed25519</c>.</param>
/// <param name="PublicKey">Die Zeile für <c>authorized_keys</c>.</param>
public sealed record KeyFile(
    string Path,
    string Name,
    KeyFormat Format,
    string Algorithm,
    int? Bits,
    string Fingerprint,
    string Comment,
    bool IsEncrypted,
    string PublicKey)
{
    /// <summary>Wer den Schlüssel benutzt: Namen von Profilen und Identitäten, mit Komma getrennt; leer = niemand.</summary>
    public string UsedBy { get; init; } = string.Empty;

    /// <summary>
    /// Art, Fingerabdruck und öffentlicher Schlüssel stammen allein aus <c>….pub</c> daneben: Die Datei selbst verrät
    /// sie ohne Passphrase nicht, und ob beide zusammengehören, hat niemand geprüft. Das steht dann dabei.
    /// </summary>
    public bool IsUnchecked { get; init; }

    /// <summary>Ob Art und Fingerabdruck bekannt sind.</summary>
    public bool IsKnown => Algorithm.Length > 0;

    /// <summary>Die Art, wie man sie sagt: „Ed25519“, „RSA 4096“, „ECDSA 256“. Leer, wenn unbekannt.</summary>
    public string Kind => Algorithm switch
    {
        "" => string.Empty,
        "ssh-ed25519" => "Ed25519",
        "ssh-rsa" => Bits is { } bits ? $"RSA {bits}" : "RSA",
        "ssh-dss" => "DSA",
        _ when Algorithm.StartsWith("ecdsa-sha2-", StringComparison.Ordinal) => Bits is { } bits ? $"ECDSA {bits}" : "ECDSA",
        _ => Algorithm,
    };

    /// <summary>Das Format, in das sich die Datei umwandeln lässt: von PuTTY nach OpenSSH, sonst nach PuTTY.</summary>
    public KeyFormat OtherFormat => Format == KeyFormat.Ppk ? KeyFormat.OpenSsh : KeyFormat.Ppk;

    /// <summary>
    /// Vorschlag für den Namen der umgewandelten Datei: mit <c>.ppk</c> dahinter bzw. ohne – und wenn die Datei von
    /// PuTTY nicht so endet, mit <c>_openssh</c>. Ob es den Namen schon gibt, sagt erst der Kern.
    /// </summary>
    public string ConvertedName
        => Format != KeyFormat.Ppk ? $"{Name}.ppk"
            : Name.EndsWith(".ppk", StringComparison.OrdinalIgnoreCase) && Name.Length > 4 ? Name[..^4]
            : $"{Name}_openssh";

    /// <summary>Name und Art – Listen lesen das Screenreadern als Bezeichnung der Zeile vor.</summary>
    public override string ToString() => Kind.Length > 0 ? $"{Name}, {Kind}" : Name;
}
