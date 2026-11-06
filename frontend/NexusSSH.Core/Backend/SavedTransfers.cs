using System.Text.Json;
using System.Text.Json.Serialization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Backend;

/// <summary>
/// Ein Transfer in <c>transfers.json</c>: alles, was ein neuer Versuch nach einem Neustart braucht, und was die
/// Zeile bis dahin zeigt. Die Sitzung von damals gibt es nicht mehr – „Wiederholen“ sucht sich über das Profil eine.
/// </summary>
/// <param name="StampLength">Upload einer Datei: ihre Größe beim ersten Versuch.</param>
/// <param name="StampTicks">Upload einer Datei: ihre Änderungszeit beim ersten Versuch (UTC, in Ticks – genau).</param>
internal sealed record SavedTransfer(
    TransferDirection Direction,
    string Name,
    string Server,
    string Local,
    string Remote,
    string? RemoteName,
    string? ProfileId,
    long? StampLength,
    long? StampTicks,
    long? Resume,
    TransferState State,
    long Bytes,
    long Total,
    string? Detail,
    long Files = 0,
    long FilesDone = 0,
    List<string>? Only = null,
    string? TargetProfileId = null,
    TransferKeep Keep = TransferKeep.None,
    List<string>? Exclude = null)
{
    /// <summary>Grund eines Transfers, der noch lief oder wartete, als die App endete (<see cref="FailureText"/>).</summary>
    public const string InterruptedDetail = "interrupted: ";

    public static SavedTransfer From(TransferItem item, TransferRequest request) => new(
        request.Direction,
        request.Name,
        request.Server,
        request.Local,
        request.Remote,
        request.RemoteName,
        request.ProfileId,
        request.Stamp?.Length,
        request.Stamp?.Modified.Ticks,
        request.Resume,
        item.State,
        item.Bytes,
        item.Total,
        item.Detail,
        item.Files,
        item.FilesDone,
        // Ohne die Auswahl würde aus einem Abgleich beim Wiederholen ein Transfer des ganzen Ordners.
        request.Only is { } only ? [.. only] : null,
        // Ohne das Ziel wäre ein Transfer von Server zu Server nach einem Neustart ein Upload ins Leere.
        request.TargetProfileId,
        // Ohne die Regel überschriebe „Wiederholen“, was der erste Versuch stehen lassen sollte.
        request.Keep,
        // Ohne die Muster nähme „Wiederholen“ mit, was der erste Versuch auslassen sollte.
        request.Exclude is { } exclude ? [.. exclude] : null);

    public TransferRequest ToRequest() => new(
        0,
        Server,
        Direction,
        Name,
        Local,
        Remote,
        RemoteName,
        ProfileId,
        StampLength is { } length && StampTicks is { } ticks ? new LocalStamp(length, new DateTime(ticks, DateTimeKind.Utc)) : null,
        Resume,
        Only,
        TargetProfileId: TargetProfileId,
        Keep: Enum.IsDefined(Keep) ? Keep : TransferKeep.None,
        // Unverändert: Die Datei kann von Hand verändert sein, und ein Muster, das keines ist, lehnt der Kern ab
        // (`invalid_exclude`). Hier auszusortieren hieße, mehr zu übertragen als beim ersten Versuch.
        Exclude: Exclude is { Count: > 0 } ? Exclude : null);

    public TransferItem ToItem(string id) => new(
        id,
        Name,
        Server,
        Direction,
        State,
        TransferItem.ProgressOf(Bytes, Total, FilesDone, Files, completed: false),
        Bytes,
        Total,
        Detail,
        Files: Files,
        FilesDone: FilesDone);

    /// <summary>
    /// Ob der Eintrag taugt. Die Datei kann von Hand geändert sein; was nicht passt, fällt weg, statt die App
    /// stolpern zu lassen. Ob die Pfade noch etwas bedeuten, prüft beim neuen Versuch der Kern wie bei jedem anderen.
    /// </summary>
    [JsonIgnore]
    public bool IsUsable =>
        Enum.IsDefined(Direction)
        && State is TransferState.Failed or TransferState.Cancelled
        && Name is { Length: > 0 }
        && Server is not null
        && Local is { Length: > 0 }
        && Remote is { Length: > 0 }
        && Bytes >= 0
        && Total >= 0
        && Files >= 0
        && FilesDone >= 0
        && Resume is null or >= 0
        && StampLength is null or >= 0
        && (StampTicks is not { } ticks || ticks >= 0 && ticks <= DateTime.MaxValue.Ticks);
}

/// <summary>Liest und schreibt <c>transfers.json</c>. Eine fehlende oder kaputte Datei heißt: Es gibt keine Transfers.</summary>
internal static class SavedTransfers
{
    /// <summary>Erhöhen, wenn sich das Format unverträglich ändert – eine ältere Datei gilt dann als leer.</summary>
    public const int CurrentVersion = 1;

    public static IReadOnlyList<SavedTransfer> Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, SavedTransfersJsonContext.Default.SavedTransfersFile) is { Version: CurrentVersion, Transfers: { } transfers }
                ? [.. transfers.OfType<SavedTransfer>().Where(transfer => transfer.IsUsable)]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Schreibt erst in eine Nachbardatei und tauscht dann. Geht das schief, bleibt die Liste im Speicher gültig;
    /// nur ein Neustart wüsste nichts davon.
    /// </summary>
    public static void Write(string path, IReadOnlyList<SavedTransfer> transfers)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new SavedTransfersFile(CurrentVersion, [.. transfers]), SavedTransfersJsonContext.Default.SavedTransfersFile));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record SavedTransfersFile(int Version, List<SavedTransfer?>? Transfers);

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(SavedTransfersFile))]
internal sealed partial class SavedTransfersJsonContext : JsonSerializerContext;
