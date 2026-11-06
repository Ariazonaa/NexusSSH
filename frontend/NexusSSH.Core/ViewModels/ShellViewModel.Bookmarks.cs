using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

// Lesezeichen für Ordner am Server. Sie gehören zum Profil, nicht zur Sitzung: Jeder Tab zum selben Server zeigt
// dieselben, und sie stehen in den Einstellungen dieses Rechners (AppSettings.Bookmarks).
public sealed partial class ShellViewModel
{
    /// <summary>Die gemerkten Ordner des Servers der aktiven Sitzung.</summary>
    public IReadOnlyList<string> ActiveBookmarks
        => ActiveSession is { } session ? FolderBookmarks.Of(Settings, session.Profile.Id) : [];

    /// <summary>Ob der Ordner, den der Dateibrowser der aktiven Sitzung zeigt, gemerkt ist.</summary>
    public bool IsCurrentFolderBookmarked
        => ActiveSession is { } session && FolderBookmarks.Contains(Settings, session.Profile.Id, session.Files.CurrentPath);

    /// <summary>Ob sich der angezeigte Ordner noch merken lässt – je Server ist die Zahl begrenzt.</summary>
    public bool CanBookmarkCurrentFolder
        => ActiveSession is { Files.IsAvailable: true } && ActiveBookmarks.Count < FolderBookmarks.MaxPerProfile;

    /// <summary>Merkt sich den angezeigten Ordner – oder vergisst ihn, wenn er schon gemerkt ist.</summary>
    public void ToggleBookmark()
    {
        if (ActiveSession is not { Files.IsAvailable: true } session)
        {
            return;
        }

        var (id, path) = (session.Profile.Id, session.Files.CurrentPath);
        if (!FolderBookmarks.Remove(Settings, id, path))
        {
            FolderBookmarks.Add(Settings, id, path);
        }

        BookmarksChanged();
    }

    /// <summary>Vergisst einen gemerkten Ordner des Servers der aktiven Sitzung.</summary>
    public void RemoveBookmark(string path)
    {
        if (ActiveSession is { } session && FolderBookmarks.Remove(Settings, session.Profile.Id, path))
        {
            BookmarksChanged();
        }
    }

    private void BookmarksChanged()
    {
        OnPropertyChanged(nameof(ActiveBookmarks));
        OnPropertyChanged(nameof(IsCurrentFolderBookmarked));
    }
}
