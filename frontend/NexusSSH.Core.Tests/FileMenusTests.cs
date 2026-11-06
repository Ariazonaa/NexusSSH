using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Symbolzeile und Menüs des Dateibrowsers (Entwurf A): kurz, aber ohne dass eine Aktion wegfällt.</summary>
public sealed class FileMenusTests
{
    private static RemoteEntry File(string name) => new(name, false, 10, DateTimeOffset.UnixEpoch);

    private static RemoteEntry Folder(string name) => new(name, true, 0, DateTimeOffset.UnixEpoch);

    private static FileSelection Of(params RemoteEntry[] selected) => FileSelection.Of(selected, canPaste: false, canUndo: false, hasMarked: false, shellConnected: true);

    [Fact]
    public void Every_action_has_a_place_and_the_first_menus_stay_short()
    {
        var everywhere = FileMenus.Toolbar.Concat(FileMenus.ViewMenu).Concat(FileMenus.Context).Concat(FileMenus.More).ToHashSet();
        Assert.Equal(Enum.GetValues<FileAction>().ToHashSet(), everywhere);

        // Sechs Symbole oben, sechs Einträge im Kontextmenü – dahinter „Mehr“.
        Assert.Equal([FileAction.Up, FileAction.Refresh, FileAction.Download, FileAction.Upload, FileAction.NewFolder, FileAction.Delete], FileMenus.Toolbar);
        Assert.Equal([FileAction.Open, FileAction.Download, FileAction.Rename, FileAction.Delete, FileAction.CopyPath, FileAction.Properties], FileMenus.Context);

        // Innerhalb einer Liste steht nichts doppelt, und hinter „Mehr“ nichts, was schon im kurzen Menü steht.
        foreach (var list in new[] { FileMenus.Toolbar, FileMenus.ViewMenu, FileMenus.Context, FileMenus.More })
        {
            Assert.Equal(list.Count, list.Distinct().Count());
        }

        Assert.Empty(FileMenus.More.Intersect(FileMenus.Context));
        Assert.Empty(FileMenus.ViewMenu.Intersect(FileMenus.Toolbar));
    }

    [Fact]
    public void What_an_action_needs_from_the_selection()
    {
        var none = Of();
        Assert.False(FileMenus.IsEnabled(FileAction.Download, none));
        Assert.False(FileMenus.IsEnabled(FileAction.Delete, none));
        Assert.False(FileMenus.IsEnabled(FileAction.Open, none));

        // Was dem angezeigten Ordner gilt, geht auch ohne Auswahl.
        Assert.True(FileMenus.IsEnabled(FileAction.CopyPath, none));
        Assert.True(FileMenus.IsEnabled(FileAction.Usage, none));
        Assert.True(FileMenus.IsEnabled(FileAction.OpenInTerminal, none));
        Assert.False(FileMenus.IsEnabled(FileAction.OpenInTerminal, FileSelection.Of([], false, false, false, shellConnected: false)));

        var file = Of(File("index.php"));
        Assert.True(FileMenus.IsEnabled(FileAction.Open, file));
        Assert.True(FileMenus.IsEnabled(FileAction.Preview, file));
        Assert.True(FileMenus.IsEnabled(FileAction.Follow, file));
        Assert.False(FileMenus.IsEnabled(FileAction.Unpack, file));
        Assert.False(FileMenus.IsEnabled(FileAction.RenameMany, file));

        var folder = Of(Folder("assets"));
        Assert.True(FileMenus.IsEnabled(FileAction.Open, folder));
        Assert.False(FileMenus.IsEnabled(FileAction.Preview, folder));
        Assert.True(FileMenus.IsEnabled(FileAction.Download, folder));

        // Eine Pipe lässt sich weder öffnen noch übertragen, aber umbenennen und löschen.
        var pipe = Of(new RemoteEntry("queue.fifo", false, 0, DateTimeOffset.UnixEpoch, IsSpecial: true));
        Assert.False(FileMenus.IsEnabled(FileAction.Open, pipe));
        Assert.False(FileMenus.IsEnabled(FileAction.Download, pipe));
        Assert.True(FileMenus.IsEnabled(FileAction.Rename, pipe));
        Assert.True(FileMenus.IsEnabled(FileAction.Delete, pipe));

        var two = Of(File("a.txt"), File("b.txt"));
        Assert.True(FileMenus.IsEnabled(FileAction.Compare, two));
        Assert.True(FileMenus.IsEnabled(FileAction.RenameMany, two));
        Assert.False(FileMenus.IsEnabled(FileAction.Rename, two));
        Assert.False(FileMenus.IsEnabled(FileAction.Properties, two));

        Assert.True(FileMenus.IsEnabled(FileAction.Unpack, Of(File("site.tar.gz"))));
        Assert.True(FileMenus.IsEnabled(FileAction.Paste, FileSelection.Of([], canPaste: true, false, false, true)));
        Assert.True(FileMenus.IsEnabled(FileAction.Undo, FileSelection.Of([], false, canUndo: true, false, true)));
    }

    [Fact]
    public void Without_commands_on_the_server_what_needs_them_is_not_offered()
    {
        Assert.True(FileMenus.IsShown(FileAction.Pack, hasCommands: true, isWindowsServer: false));
        Assert.False(FileMenus.IsShown(FileAction.Pack, hasCommands: false, isWindowsServer: false));
        Assert.False(FileMenus.IsShown(FileAction.Usage, hasCommands: false, isWindowsServer: false));

        // Unter Windows (PowerShell) fehlt nur, was eine POSIX-Shell braucht.
        Assert.True(FileMenus.IsShown(FileAction.Unpack, hasCommands: true, isWindowsServer: true));
        Assert.False(FileMenus.IsShown(FileAction.Follow, hasCommands: true, isWindowsServer: true));
        Assert.False(FileMenus.IsShown(FileAction.Root, hasCommands: true, isWindowsServer: true));

        // Kopieren und Duplizieren gehen auch ohne Befehle – über die Dateiverbindung.
        Assert.True(FileMenus.IsShown(FileAction.Duplicate, hasCommands: false, isWindowsServer: false));
        Assert.True(FileMenus.IsShown(FileAction.Paste, hasCommands: false, isWindowsServer: false));
    }
}
