using NexusSSH.Core.Services;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Der Zwischenordner für Dateien, die ein anderes Programm aus dem Dateibrowser bekommt. Wann geholt wird,
/// entscheidet das Angebot (<see cref="VirtualDropTests"/>).
/// </summary>
public sealed class DropFetcherTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Every_fetch_gets_a_folder_of_its_own()
    {
        var work = Path.Combine(_temp.Path, "drag");
        using var fetcher = new DropFetcher(work);

        var first = fetcher.NewFolder();
        var second = fetcher.NewFolder();

        Assert.NotEqual(first, second);
        Assert.All([first, second], folder =>
        {
            Assert.True(Directory.Exists(folder));
            Assert.Equal(work, Path.GetDirectoryName(folder));
        });
    }

    [Fact]
    public void The_folder_disappears_with_the_app_and_stale_ones_at_the_next_start()
    {
        var mine = DropFetcher.WorkDirectory(_temp.Path);
        var stale = Path.Combine(_temp.Path, "drag-999999");
        var editor = Path.Combine(_temp.Path, "edit-999999");
        Directory.CreateDirectory(Path.Combine(mine, "x"));
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(editor);

        // Nur die Ordner fürs Ziehen, und nur die von Prozessen, die es nicht mehr gibt.
        DropFetcher.RemoveStale(_temp.Path, process => process == Environment.ProcessId);
        Assert.True(Directory.Exists(mine));
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(editor));

        new DropFetcher(mine).Dispose();
        Assert.False(Directory.Exists(mine));
    }
}
