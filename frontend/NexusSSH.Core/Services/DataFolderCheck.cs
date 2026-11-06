namespace NexusSSH.Core.Services;

/// <summary>
/// Ein Eintrag der Zugriffsliste eines Ordners, so weit er hier zählt: wer, und ob ihm damit das Schreiben erlaubt ist.
/// </summary>
/// <param name="Sid">Die Kennung des Kontos oder der Gruppe (<c>S-1-5-…</c>).</param>
/// <param name="Name">Der Name dazu, wenn Windows ihn kennt – sonst die Kennung.</param>
/// <param name="Rights">Die erlaubten Rechte als Bits, wie Windows sie führt.</param>
public sealed record FolderGrant(string Sid, string Name, int Rights);

/// <summary>
/// Prüft, wem der Datenordner gehört. Die App vertraut ihm ganz: Dort steht, welches Programm „Bearbeiten“ und
/// „Vergleichen“ starten, welche Transfers sich wiederholen lassen, welchen Servern und Zertifikaten sie traut und
/// wohin ein Profil führt. Wer dort schreiben kann, kann das alles ändern – das soll nur der Benutzer selbst sein
/// (und, wer den Rechner verwaltet). Ob es so ist, sagt die Zugriffsliste des Ordners; die liest die Oberfläche,
/// hier steht nur die Auswertung.
/// </summary>
public static class DataFolderCheck
{
    // Schreiben, anhängen, Unterordner leeren, Rechte ändern, Besitz übernehmen – und die allgemeinen Rechte
    // „Schreiben“ und „Alles“, wie sie in vererbten Einträgen stehen.
    private const int WriteData = 0x2;
    private const int AppendData = 0x4;
    private const int DeleteChild = 0x40;
    private const int Delete = 0x10000;
    private const int ChangePermissions = 0x40000;
    private const int TakeOwnership = 0x80000;
    private const int GenericWrite = 0x40000000;
    private const int GenericAll = 0x10000000;
    private const int Writes = WriteData | AppendData | DeleteChild | Delete | ChangePermissions | TakeOwnership | GenericWrite | GenericAll;

    // Wer den Rechner verwaltet, kann ohnehin alles: das System, die Administratoren, der Installer von Windows.
    // „Ersteller-Besitzer“ und „Besitzerrechte“ meinen den, der eine Datei angelegt hat – hier den Benutzer selbst.
    private static readonly string[] Trusted = ["S-1-5-18", "S-1-5-32-544", "S-1-3-0", "S-1-3-4"];
    private const string TrustedInstallerPrefix = "S-1-5-80-";

    /// <summary>
    /// Die Namen aller, die außer dem Benutzer und den Verwaltern des Rechners im Ordner schreiben dürfen – jeder
    /// einmal, in der Reihenfolge der Liste. Leer heißt: Der Ordner gehört dem Benutzer allein.
    /// </summary>
    public static IReadOnlyList<string> OthersWhoCanWrite(IEnumerable<FolderGrant> grants, string ownSid)
    {
        var others = new List<string>();
        foreach (var grant in grants)
        {
            if ((grant.Rights & Writes) == 0
                || string.Equals(grant.Sid, ownSid, StringComparison.OrdinalIgnoreCase)
                || Trusted.Contains(grant.Sid, StringComparer.OrdinalIgnoreCase)
                || grant.Sid.StartsWith(TrustedInstallerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Der Name kommt aus dem Verzeichnis des Rechners oder der Domäne – wie jeder fremde Text ohne Steuerzeichen.
            var name = Format.Visible(grant.Name is { Length: > 0 } ? grant.Name : grant.Sid);
            if (!others.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                others.Add(name);
            }
        }

        return others;
    }
}
