using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Zwei Dateien im Vergleichsprogramm: Server gegen Server, Server gegen diesen Rechner.</summary>
[Collection(LanguageCollection.Name)]
public sealed class CompareTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeEditorHost _host = new();
    private readonly MockBackend _backend = new();
    private readonly ShellViewModel _shell;

    public CompareTests()
    {
        L.Init(AppLanguage.English);
        _shell = new ShellViewModel(
            _backend,
            new ImmediateDispatcher(),
            new AppSettings(),
            new FakePrompts(),
            _host,
            Path.Combine(_temp.Path, "edit"),
            Path.Combine(_temp.Path, "drop"));
    }

    public void Dispose()
    {
        _shell.Dispose();
        _temp.Dispose();
    }

    private async Task<SessionViewModel> SessionAsync(string name = "web01")
    {
        var profile = _backend.GetProfiles().Single(profile => profile.Id == "web01");
        var session = _shell.OpenSession(name == "web01" ? profile : profile with { Id = name, Name = name });
        session.Start();
        await session.Files.LoadAsync();
        return session;
    }

    // Die Beispieldaten übertragen nicht von selbst: Jeder Download, der ansteht, wird hier für fertig erklärt.
    private async Task<bool> FinishAsync(Task<bool> comparing)
    {
        var queue = (MockTransferQueue)_backend.Transfers;
        var patience = System.Diagnostics.Stopwatch.StartNew();
        while (!comparing.IsCompleted && patience.Elapsed < TimeSpan.FromSeconds(10))
        {
            foreach (var item in queue.Items.Where(item => item.State is not (TransferState.Completed or TransferState.Failed or TransferState.Cancelled)).ToList())
            {
                queue.Complete(item.Id);
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(comparing.IsCompleted, "the comparison did not finish");
        return await comparing;
    }

    [Fact]
    public async Task Two_files_of_a_server_are_fetched_and_handed_to_the_program()
    {
        var session = await SessionAsync();
        var files = session.Files.Entries.Where(entry => !entry.IsDirectory).Take(2).ToList();

        Assert.True(await FinishAsync(_shell.CompareAsync(session, files[0], session.Files.PathOf(files[0]), session, files[1], session.Files.PathOf(files[1]))));

        var (left, right) = Assert.Single(_host.Compared);
        Assert.EndsWith(files[0].Name, left);
        Assert.EndsWith(files[1].Name, right);
        // Jede Datei liegt in ihrem eigenen Ordner – zwei gleichen Namens kämen sich sonst in die Quere.
        Assert.NotEqual(Path.GetDirectoryName(left), Path.GetDirectoryName(right));
        Assert.Contains(_shell.Output, entry => entry.Text == $"Comparison opened: {files[0].Name} and {files[1].Name}.");
    }

    [Fact]
    public async Task A_marked_file_is_compared_with_one_from_another_server()
    {
        var first = await SessionAsync();
        var second = await SessionAsync("web02");
        var file = first.Files.Entries.First(entry => !entry.IsDirectory);
        Assert.False(await _shell.CompareWithMarkedAsync(second, file));

        _shell.MarkForCompare(first, file);
        Assert.Equal($"{file.Name} (web01)", _shell.CompareMarked!.Label);

        Assert.True(await FinishAsync(_shell.CompareWithMarkedAsync(second, file)));

        Assert.Single(_host.Compared);
        Assert.Null(_shell.CompareMarked);
    }

    [Fact]
    public async Task A_local_file_stands_on_the_left_and_is_not_copied()
    {
        var session = await SessionAsync();
        var file = session.Files.Entries.First(entry => !entry.IsDirectory);
        var local = Path.Combine(_temp.Path, "mine.conf");

        Assert.True(await FinishAsync(_shell.CompareWithLocalAsync(session, file, local)));

        var (left, right) = Assert.Single(_host.Compared);
        Assert.Equal(local, left);
        Assert.EndsWith(file.Name, right);
    }

    [Theory]
    [InlineData(CompareOutcome.NoProgram, "No program is set for comparing")]
    [InlineData(CompareOutcome.NotAProgram, "The program set for comparing is a script")]
    [InlineData(CompareOutcome.Failed, "The comparison program could not be started.")]
    public async Task Without_a_usable_program_the_log_says_why(CompareOutcome outcome, string expected)
    {
        _host.Comparing = outcome;
        var session = await SessionAsync();
        var file = session.Files.Entries.First(entry => !entry.IsDirectory);

        Assert.False(await FinishAsync(_shell.CompareWithLocalAsync(session, file, Path.Combine(_temp.Path, "mine.conf"))));

        Assert.Contains(_shell.Problems, entry => entry.Text.StartsWith(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Folders_and_files_too_large_to_fetch_are_not_compared()
    {
        var session = await SessionAsync();
        var folder = session.Files.Entries.First(entry => entry.IsDirectory);
        var huge = new RemoteEntry("big.iso", false, RemoteEditor.MaxSize + 1, DateTimeOffset.MinValue);
        var before = _backend.Transfers.Items.Count;

        Assert.False(await _shell.CompareWithLocalAsync(session, folder, "x"));
        Assert.False(await _shell.CompareWithLocalAsync(session, huge, "x"));

        Assert.Empty(_host.Compared);
        Assert.Equal(before, _backend.Transfers.Items.Count);
    }

    [Fact]
    public void The_program_gets_the_two_files_as_arguments_and_scripts_are_refused()
    {
        Assert.Equal(["a", "b"], ProcessEditorHost.CompareArguments(@"C:\Program Files\WinMerge\WinMergeU.exe", "a", "b"));
        Assert.Equal(["--diff", "a", "b"], ProcessEditorHost.CompareArguments(Path.Combine("Programs", "Microsoft VS Code", "Code.exe"), "a", "b"));

        Assert.True(ProcessEditorHost.IsScript(@"C:\tools\code.CMD"));
        Assert.True(ProcessEditorHost.IsScript("compare.bat"));
        Assert.False(ProcessEditorHost.IsScript(@"C:\Program Files\WinMerge\WinMergeU.exe"));

        // Ohne Einstellung und mit einem Skript startet nichts.
        Assert.Equal(CompareOutcome.NoProgram, new ProcessEditorHost(() => "x").Compare("a", "b"));
        Assert.Equal(CompareOutcome.NoProgram, new ProcessEditorHost(() => "x", () => " ").Compare("a", "b"));
        Assert.Equal(CompareOutcome.NotAProgram, new ProcessEditorHost(() => "x", () => "code.cmd").Compare("a", "b"));
    }
}
