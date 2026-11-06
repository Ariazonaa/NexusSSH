using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

[Collection(LanguageCollection.Name)]
public sealed class MockAndViewModelTests
{
    public MockAndViewModelTests() => L.Init(AppLanguage.English);

    [Fact]
    public void Transfers_show_the_three_states_from_the_spec()
    {
        using var queue = new MockTransferQueue();
        using var center = new TransferCenterViewModel(queue, new ImmediateDispatcher());

        Assert.Equal(["backup.tar.gz", "nginx.conf", "database.sql"], center.Items.Select(item => item.FileName));
        Assert.Equal(["Uploading – 72%", "Completed", "Download queued"], center.Items.Select(item => item.StatusText));
        Assert.Equal([true, false, false], center.Items.Select(item => item.IsRunning));
    }

    [Fact]
    public void The_queue_of_the_demo_data_stands_still_even_with_a_running_clock()
    {
        // Der Oberflächentest hält den wartenden Transfer an – auf einem langsamen Rechner erst nach Minuten.
        using var backend = new MockBackend(new TickingClock());

        Assert.Equal([TransferState.Running, TransferState.Completed, TransferState.Queued], backend.Transfers.Items.Select(item => item.State));
        Assert.Equal(0.72, backend.Transfers.Items[0].Progress, precision: 6);
    }

    // Eine Uhr, deren Timer sofort hundertmal auslösen – als wäre viel Zeit vergangen.
    private sealed class TickingClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            for (var i = 0; i < 100; i++)
            {
                callback(state);
            }

            return new Idle();
        }

        private sealed class Idle : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void A_running_transfer_advances_in_place_then_the_queued_one_starts()
    {
        using var queue = new MockTransferQueue();
        using var center = new TransferCenterViewModel(queue, new ImmediateDispatcher());
        var upload = center.Items[0];
        var download = center.Items[2];

        queue.Advance();
        Assert.Same(upload, center.Items[0]);
        Assert.Equal(74, upload.ProgressPercent, precision: 6);

        for (var i = 0; i < 20 && upload.IsRunning; i++)
        {
            queue.Advance();
        }

        Assert.Equal("Completed", upload.StatusText);
        Assert.False(download.IsRunning);

        queue.Advance();
        Assert.True(download.IsRunning);
        Assert.Equal("Downloading – 0%", download.StatusText);
    }

    [Fact]
    public void The_queue_comes_to_rest_when_everything_is_done()
    {
        using var queue = new MockTransferQueue();
        var changes = 0;
        queue.Changed += () => changes++;

        for (var i = 0; i < 200; i++)
        {
            queue.Advance();
        }

        Assert.All(queue.Items, item => Assert.Equal(TransferState.Completed, item.State));
        var settled = changes;
        queue.Advance();
        Assert.Equal(settled, changes);
    }

    [Fact]
    public void The_inspector_follows_the_monitor_until_disposed()
    {
        var profile = new ServerProfile("web01", "web01", "10.0.1.10", 22, "ubuntu", "Production", []);
        var baseline = new ServerStats(true, 34, 42, 28, "Ubuntu 24.04.1 LTS", "6.8.0-45-generic", TimeSpan.FromDays(32));
        using var monitor = new MockServerMonitor(baseline, seed: 1);
        var inspector = new ServerInspectorViewModel(profile, new ImmediateDispatcher());
        Assert.Equal("Offline", inspector.StatusText);
        Assert.False(inspector.HasMetrics);

        inspector.SetConnection(online: true, "Online");
        inspector.Attach(monitor);
        inspector.SetWatched(true);

        Assert.Equal("Online", inspector.StatusText);
        Assert.True(inspector.HasMetrics);
        Assert.Equal("34%", inspector.CpuText);
        Assert.Equal("32 days", inspector.UptimeText);

        monitor.Advance();
        Assert.Equal(monitor.Current!.CpuPercent, inspector.CpuPercent);
        Assert.InRange(inspector.CpuPercent, 22, 46);
        Assert.Equal(28, inspector.DiskPercent);

        inspector.Dispose();
        var frozen = inspector.CpuPercent;
        monitor.Advance();
        monitor.Advance();
        Assert.Equal(frozen, inspector.CpuPercent);
    }

    [Fact]
    public async Task Every_profile_has_a_listable_start_directory()
    {
        using var backend = new MockBackend();
        foreach (var profile in backend.GetProfiles())
        {
            using var connection = backend.Connect(profile);
            var files = Assert.IsAssignableFrom<Services.IRemoteFileSystem>(connection.Files);
            var entries = await files.ListAsync(files.InitialPath, TestContext.Current.CancellationToken);
            Assert.NotEmpty(entries);
        }
    }

    [Fact]
    public async Task The_demo_home_has_every_kind_of_link()
    {
        using var backend = new MockBackend();
        var profile = backend.GetProfiles()[0];
        using var connection = backend.Connect(profile);
        var files = Assert.IsAssignableFrom<Services.IRemoteFileSystem>(connection.Files);
        var home = profile.User == "root" ? "/root" : $"/home/{profile.User}";
        var entries = (await files.ListAsync(home, TestContext.Current.CancellationToken)).ToDictionary(entry => entry.Name);

        Assert.True(entries["skripte"] is { IsLink: true, IsDirectory: true, IsDangling: false });
        Assert.True(entries["notizen"] is { IsLink: true, IsDirectory: false, IsDangling: false });
        Assert.True(entries["verwaist"] is { IsLink: true, IsDangling: true });
        Assert.True(entries["langes-ziel"].LinkTarget!.Length >= 3900);
        Assert.True(entries["unbekannt"] is { IsLink: true, LinkTarget: null, IsDangling: false });
        Assert.Equal(home + "/.profile", await files.ReadLinkAsync(home + "/unbekannt", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(912, "912 B")]
    [InlineData(1_834, "1.8 KB")]
    [InlineData(248_320, "243 KB")]
    [InlineData(6_291_456, "6.0 MB")]
    [InlineData(734_003_200, "700 MB")]
    public void File_sizes_use_binary_units(long bytes, string expected)
        => Assert.Equal(expected, Format.FileSize(bytes, isDirectory: false));

    [Fact]
    public void Directories_have_no_size_and_numbers_follow_the_language()
    {
        Assert.Equal(string.Empty, Format.FileSize(4096, isDirectory: true));

        L.Init(AppLanguage.German);
        Assert.Equal("1,8 KB", Format.FileSize(1_834, isDirectory: false));
        Assert.Equal("34 %", Format.Percent(34.4));
        Assert.Equal("19 Std.", Format.Uptime(TimeSpan.FromHours(19)));
    }
}
