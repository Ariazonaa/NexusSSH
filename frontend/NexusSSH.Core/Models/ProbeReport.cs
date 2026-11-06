namespace NexusSSH.Core.Models;

/// <summary>
/// Ergebnis eines Verbindungstests: wie weit der Weg zu einem Server trägt, ohne dass sich jemand anmeldet.
/// Felder bleiben leer, wenn der Test nicht so weit kam; <see cref="Step"/> nennt den letzten Schritt, der gelang.
/// </summary>
/// <param name="Step"><c>resolve</c>, <c>connect</c>, <c>greeting</c>, <c>key</c>, <c>methods</c>; leer, wenn keiner gelang.</param>
/// <param name="Addresses">Die Adressen, die der Name ergibt.</param>
/// <param name="Address">Die Adresse, die geantwortet hat.</param>
/// <param name="ConnectMs">Dauer des Verbindungsaufbaus in Millisekunden.</param>
/// <param name="Software">Als was sich der Dienst vorstellt – Text des Servers, schon gekürzt und ohne Steuerzeichen.</param>
/// <param name="KeyState"><c>trusted</c>, <c>unknown</c> oder <c>changed</c>.</param>
/// <param name="Methods">Die Anmeldeverfahren, die der Server anbietet.</param>
/// <param name="Trouble">Woran es scheiterte (<see cref="Services.ConnectTrouble"/>), sonst <c>null</c>.</param>
/// <param name="Detail">Die englische Einzelheit des Kerns dazu.</param>
public sealed record ProbeReport(
    string Step,
    IReadOnlyList<string> Addresses,
    string? Address = null,
    int? ConnectMs = null,
    string? Software = null,
    string? KeyAlgorithm = null,
    string? Fingerprint = null,
    string? KeyState = null,
    IReadOnlyList<string>? Methods = null,
    string? Trouble = null,
    string? Detail = null)
{
    public const string Trusted = "trusted";

    /// <summary>Vertraut über ein Zertifikat, das eine bekannte Zertifizierungsstelle unterschrieben hat.</summary>
    public const string Certified = "certified";
    public const string Unknown = "unknown";
    public const string Changed = "changed";

    /// <summary>Der Test kam bis zu den Anmeldeverfahren (oder, bei Telnet, bis zum offenen Port) und stieß auf nichts.</summary>
    public bool Succeeded => Trouble is null;
}
