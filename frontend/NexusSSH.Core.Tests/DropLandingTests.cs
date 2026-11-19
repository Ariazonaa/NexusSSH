using NexusSSH.Core.Services;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Wohin der Explorer etwas abgelegt hat: Er kopiert einen leeren Merker-Ordner, und dessen Kopie verrät das Ziel.
/// Hier „kopiert“ der Test selbst, in einen beobachteten Ordner.
/// </summary>
public sealed class DropLandingTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TempFolder _temp = new();
    private readonly string _root;
    private readonly string _home;

    public DropLandingTests()
    {
        _root = Path.Combine(_temp.Path, "laufwerk");
        _home = Path.Combine(_temp.Path, "merker");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task The_folder_of_the_copied_marker_is_the_target_and_the_copy_is_removed_afterwards()
    {
        // Den Zielordner gibt es schon, wenn gezogen wird (unter Linux sähe ein Wächter einen eben angelegten
        // Unterordner erst verzögert).
        var target = Path.Combine(_root, "Projekte", "neu");
        Directory.CreateDirectory(target);
        using var landing = new DropLanding(_home, () => [_root]);
        await landing.Ready;
        Assert.True(Directory.Exists(landing.Marker));
        Assert.StartsWith(DropLanding.MarkerPrefix, Path.GetFileName(landing.Marker));

        var copy = Path.Combine(target, Path.GetFileName(landing.Marker));
        Directory.CreateDirectory(copy);

        Assert.Equal(target, await landing.TargetAsync(Patience));

        // Die Kopie bleibt, bis der Explorer fertig ist – er greift noch auf sie zu.
        Assert.True(Directory.Exists(copy));
        await landing.RemoveCopyAsync();
        Assert.False(Directory.Exists(copy));
    }

    [Fact]
    public async Task Other_folders_do_not_count_and_without_a_copy_there_is_no_target()
    {
        using var landing = new DropLanding(_home, () => [_root]);
        await landing.Ready;
        Directory.CreateDirectory(Path.Combine(_root, "irgendwas"));
        Directory.CreateDirectory(Path.Combine(_root, DropLanding.MarkerPrefix + "fremd"));

        Assert.Null(await landing.TargetAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task The_marker_itself_is_no_target_even_where_it_is_watched()
    {
        // Liegt der Merker selbst unter einer beobachteten Wurzel (dem Laufwerk mit dem Temp-Ordner), zählt er nicht.
        using var landing = new DropLanding(Path.Combine(_root, "temp", "merker"), () => [_root]);
        await landing.Ready;
        Directory.Delete(landing.Marker);
        Directory.CreateDirectory(landing.Marker);

        Assert.Null(await landing.TargetAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task Dispose_removes_the_marker_and_unwatchable_roots_are_skipped()
    {
        var landing = new DropLanding(_home, () => [_root, Path.Combine(_temp.Path, "gibt-es-nicht")]);
        await landing.Ready;
        var marker = landing.Marker;
        landing.Dispose();

        Assert.False(Directory.Exists(marker));
        Assert.False(Directory.Exists(_home));
    }
}
