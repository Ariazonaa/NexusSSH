using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// Vorschau im Dateibrowser: eine Textdatei oder ein Bild ansehen, ohne einen Editor zu starten. Die Datei wird
// wie beim Ziehen in einen Arbeitsordner geholt, der mit der App verschwindet (DropFetcher).
public sealed partial class ShellViewModel
{
    /// <summary>
    /// Holt eine Datei des Servers für die Vorschau. Es kommt immer etwas zurück: der Inhalt – oder in
    /// <see cref="PreviewContent.Problem"/>, warum es keinen gibt.
    /// </summary>
    public async Task<PreviewContent> PreviewAsync(SessionViewModel session, RemoteEntry entry)
    {
        var name = entry.DisplayName;
        if (_drops is null || session.Files.FileSystem is not { } files || entry.IsDirectory || entry.IsSpecial)
        {
            return new PreviewContent(name, PreviewKind.None, Problem: L.Get("PreviewUnavailable"));
        }

        var kind = RemotePreview.KindOf(entry.Name, entry.Size);
        if (kind == PreviewKind.None)
        {
            return new PreviewContent(name, kind, Problem: L.Get("PreviewTooLarge", Format.FileSize(entry.Size, isDirectory: false)));
        }

        var (local, failure) = await RemotePreview.FetchAsync(_backend.Transfers, files, session.Files.PathOf(entry), _drops.NewFolder());
        if (local is null)
        {
            return new PreviewContent(name, PreviewKind.None, Problem: FailureText.OfTransfer(failure));
        }

        if (kind == PreviewKind.Image)
        {
            return new PreviewContent(name, kind, ImagePath: local);
        }

        try
        {
            return RemotePreview.ReadText(await File.ReadAllBytesAsync(local)) is var (text, truncated)
                ? new PreviewContent(name, kind, text, Truncated: truncated)
                : new PreviewContent(name, PreviewKind.None, Problem: L.Get("PreviewNotText"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new PreviewContent(name, PreviewKind.None, Problem: e.Message);
        }
    }
}
