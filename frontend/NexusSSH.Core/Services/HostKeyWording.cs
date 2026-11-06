using NexusSSH.Core.Localization;

namespace NexusSSH.Core.Services;

/// <summary>Die Sätze der Rückfrage zum Host-Schlüssel, die von den Umständen abhängen.</summary>
public static class HostKeyWording
{
    /// <summary>So nennt der Kern das „Verfahren“, wenn er nach einem TLS-Zertifikat fragt (FTPS) statt nach einem Host-Schlüssel.</summary>
    public const string Certificate = "x509";

    public static bool IsCertificate(ConnectionPrompt.HostKey prompt) => prompt.Algorithm == Certificate;

    public static string Title(ConnectionPrompt.HostKey prompt) => IsCertificate(prompt)
        ? prompt.Changed ? L.Get("TlsChangedTitle") : L.Get("TlsUnknownTitle")
        : prompt.Changed ? L.Get("HostKeyChangedTitle") : L.Get("HostKeyUnknownTitle");

    public static string Intro(ConnectionPrompt.HostKey prompt) => IsCertificate(prompt)
        ? prompt.Changed ? L.Get("TlsChangedIntro") : L.Get("TlsUnknownIntro")
        : prompt.Changed ? L.Get("HostKeyChangedIntro") : L.Get("HostKeyUnknownIntro");

    /// <summary>Die Warnung über einem geänderten Schlüssel oder Zertifikat.</summary>
    public static string Warning(ConnectionPrompt.HostKey prompt)
        => IsCertificate(prompt) ? L.Get("TlsChangedWarning") : L.Get("HostKeyChangedWarning");

    /// <summary>Was unter dem Fingerabdruck steht: was eine Zustimmung bewirkt bzw. wo der alte Eintrag steht.</summary>
    public static string Hint(ConnectionPrompt.HostKey prompt) => prompt.Changed
        ? IsCertificate(prompt) ? string.Empty : ChangedHint(prompt)
        : IsCertificate(prompt) ? L.Get("TlsUnknownHint") : L.Get("HostKeyUnknownHint");

    public static string Accept(ConnectionPrompt.HostKey prompt) => prompt.Changed
        ? IsCertificate(prompt) ? L.Get("TlsReplace") : L.Get("HostKeyReplace")
        : L.Get("HostKeyTrust");

    /// <summary>Die Zeile „Verfahren“: bei einem Zertifikat ein Wort dafür statt der Kennung des Kerns.</summary>
    public static string Kind(ConnectionPrompt.HostKey prompt) => IsCertificate(prompt) ? L.Get("TlsKind") : prompt.Algorithm;

    /// <summary>
    /// Bei geändertem Schlüssel: wo der alte Eintrag steht – und was mit ihm geschieht, wenn der Benutzer dem neuen
    /// vertraut. In der Liste der App wird er ersetzt; in <c>~/.ssh/known_hosts</c> des Benutzers bleibt er stehen
    /// (die App ändert diese Datei nie), dort müsste man die Zeile selbst entfernen, damit auch <c>ssh</c> den
    /// Server wieder ohne Warnung annimmt.
    /// </summary>
    public static string ChangedHint(ConnectionPrompt.HostKey prompt)
    {
        if (prompt.KnownHostsFile is not { Length: > 0 } file)
        {
            return string.Empty;
        }

        var where = L.Get("HostKeyChangedHint", file, prompt.KnownHostsLine ?? 0);
        return prompt.KnownHostsForeign
            ? $"{where} {L.Get("HostKeyChangedForeign", prompt.KnownHostsLine ?? 0)}"
            : $"{where} {L.Get("HostKeyChangedOwn")}";
    }
}
