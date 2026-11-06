using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// Von Server zu Server: Einträge der einen Sitzung in den angezeigten Ordner einer anderen schicken. Der Weg führt
// über den Zwischenordner dieses Rechners (ServerRelay) – die beiden Server müssen einander nicht erreichen.
public sealed partial class ShellViewModel
{
    private ServerRelay? _relay;

    /// <summary>Die anderen Sitzungen, in deren Dateibrowser sich etwas schicken lässt – verbunden und mit Dateizugriff.</summary>
    public IReadOnlyList<SessionViewModel> RelayTargets(SessionViewModel from)
        => [.. Sessions.Where(session => session != from && session.State == ConnectionState.Connected && session.Files.IsAvailable)];

    /// <summary>Welche der Einträge es im angezeigten Ordner des Ziels schon gibt – für die Rückfrage vor dem Senden.</summary>
    public static IReadOnlyList<string> ExistingAt(SessionViewModel to, IEnumerable<RemoteEntry> entries)
        => to.Files.ExistingNames(entries.Where(entry => entry.IsTransferable).Select(entry => entry.Name));

    /// <summary>
    /// Schickt die Einträge in den Ordner, den der Dateibrowser von <paramref name="to"/> gerade zeigt. Liefert, wie
    /// viele auf den Weg gebracht wurden; was sich nicht übertragen lässt (Pipes, Geräte), bleibt aus.
    /// </summary>
    /// <param name="rule">Wie beim Hochladen: was mit dem geschieht, was es am Ziel schon gibt – einmal für alles.</param>
    public int SendToServer(SessionViewModel from, IReadOnlyList<RemoteEntry> entries, SessionViewModel to, ExistingRule rule = ExistingRule.Overwrite, IReadOnlyList<string>? exclude = null)
    {
        if (_relay is not { } relay || from.Files.FileSystem is not { } source || to.Files.FileSystem is not { } target)
        {
            return 0;
        }

        var directory = to.Files.CurrentPath;
        var taken = to.Files.TakenNames();
        var sent = 0;
        foreach (var entry in entries.Where(entry => entry.IsTransferable))
        {
            // „Umbenennen“: Was es am Ziel schon gibt, bleibt; das Neue kommt daneben, unter einem freien Namen.
            string? other = null;
            if (rule == ExistingRule.Rename && taken.Contains(entry.Name))
            {
                other = RenamePlan.CopyName(entry.Name, taken);
                taken.Add(other);
            }

            // Wenn das Backend es kann, direkt von Server zu Server; sonst über den Zwischenordner dieses Rechners.
            var path = from.Files.PathOf(entry);
            if (source.SendTo(path, target, directory, other, rule.Keep(), exclude) is not null
                || relay.Send(source, path, target, directory, to.Profile.Name, other, rule.Keep(), exclude))
            {
                sent++;
            }
            else
            {
                Log(L.Get("LogRelayBadName", entry.DisplayName), LogSeverity.Warning);
            }
        }

        if (sent > 0)
        {
            Log(L.Get("LogRelayStarted", sent, from.Profile.Name, to.Profile.Name, directory));
        }

        return sent;
    }

    private void UseRelay(DropFetcher folders)
    {
        _relay = new ServerRelay(_backend.Transfers, folders);
        _relay.Failed += (file, server, detail) =>
            _dispatcher.Post(() => Log(L.Get("LogTransferFailed", file, server, detail), LogSeverity.Error));
    }
}
