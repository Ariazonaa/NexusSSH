using NexusSSH.Core;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Views;

/// <summary>
/// Einträge des Servers in einen Ordner dieses Rechners laden – nach den Fragen, die jeder Download stellt: ob
/// dort etwas überschrieben würde und welche Ausschlüsse gelten. Für die lokale Spalte der Ansicht „Zwei Spalten“
/// und für das Ablegen im Explorer.
/// </summary>
internal static class FolderDownload
{
    /// <summary>Liefert, ob geladen wird (und nicht abgebrochen wurde oder nichts zu laden war).</summary>
    public static async Task<bool> AskAndStartAsync(SessionViewModel session, IReadOnlyList<RemoteEntry> entries, string folder, Func<ExistingRule, IReadOnlyList<string>?, bool> start)
    {
        var wanted = entries.Where(entry => entry.IsTransferable).ToList();
        if (!session.Files.IsAvailable || wanted.Count == 0)
        {
            return false;
        }

        var rule = ExistingRule.Overwrite;
        var existing = session.Files.ExistingLocally(wanted, folder);
        if (existing.Count > 0)
        {
            if (await App.Dialogs.AskExistingAsync(L.Get("OverwriteLocalText", string.Join(", ", existing.Select(Format.Visible))), App.Shell.Settings) is not { } chosen)
            {
                return false;
            }

            rule = chosen;
        }

        if (await App.Dialogs.AskExcludesAsync(App.Shell.ExcludeRules, rules => session.Files.FindExcludedAsync(rules, wanted)) is not { } exclude)
        {
            return false;
        }

        return start(rule, exclude);
    }

    /// <summary>Wie <see cref="AskAndStartAsync"/>, geladen wird in <paramref name="folder"/>.</summary>
    public static Task<bool> AskAndStartAsync(SessionViewModel session, IReadOnlyList<RemoteEntry> entries, string folder)
        => AskAndStartAsync(session, entries, folder, (rule, exclude) =>
        {
            session.Files.Download([.. entries.Where(entry => entry.IsTransferable)], folder, rule, exclude);
            return true;
        });
}
