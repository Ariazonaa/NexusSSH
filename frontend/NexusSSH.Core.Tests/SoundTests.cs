using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Töne zu Ereignissen: welche von Haus aus kommen, welche nie, und dass keiner sich häuft.</summary>
[Collection(LanguageCollection.Name)]
public sealed class SoundTests
{
    private static readonly ServerProfile Profile = new("p1", "web01", "10.0.1.10", 22, "ubuntu", "Production", []);
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly ScriptedBackend _backend = new(Profile);
    private readonly List<Action> _scheduled = [];
    private readonly List<(SoundEvent Sound, bool OnlyInBackground)> _heard = [];
    private DateTimeOffset _clock = Start;

    private ShellViewModel Shell(AppSettings? settings = null)
    {
        L.Init(AppLanguage.English);
        var shell = new ShellViewModel(_backend, new ImmediateDispatcher(), settings ?? new AppSettings(), new FakePrompts(), schedule: (_, run) =>
        {
            _scheduled.Add(run);
            return new Nothing();
        });
        shell.Now = () => _clock;
        shell.SoundRequested += (sound, onlyInBackground) => _heard.Add((sound, onlyInBackground));
        return shell;
    }

    private ScriptedConnection Open(ShellViewModel shell)
    {
        shell.OpenSession(Profile).Start();
        return _backend.Last;
    }

    private IEnumerable<SoundEvent> Heard => _heard.Select(entry => entry.Sound);

    [Fact]
    public void Failures_sound_from_the_start_and_good_news_only_when_chosen()
    {
        var shell = Shell();

        Open(shell).Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectFailed, "connection refused"));
        Assert.Equal([SoundEvent.ConnectFailed], Heard);

        Open(shell).Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.AuthFailed, "no sign-in method was accepted"));
        Assert.Equal([SoundEvent.ConnectFailed, SoundEvent.AuthFailed], Heard);

        Open(shell).Set(new ConnectionStatus(ConnectionState.Connected));
        Assert.Equal(2, _heard.Count);

        shell.SoundConnected = true;
        Open(shell).Set(new ConnectionStatus(ConnectionState.Connected));
        Assert.Equal(SoundEvent.Connected, Heard.Last());
        Assert.All(_heard, entry => Assert.False(entry.OnlyInBackground));
    }

    [Theory]
    [InlineData(CloseReason.User, null)]
    [InlineData(CloseReason.Remote, null)]
    [InlineData(CloseReason.HostKeyRejected, null)]
    [InlineData(CloseReason.AuthFailed, "the sign-in was cancelled")]
    [InlineData(CloseReason.AuthFailed, "jump host demo@127.0.0.1:2222: the sign-in was cancelled")]
    public void An_end_the_user_wanted_is_silent(CloseReason reason, string? detail)
    {
        var shell = Shell();
        var connection = Open(shell);
        connection.Set(new ConnectionStatus(ConnectionState.Connected));

        connection.Set(new ConnectionStatus(ConnectionState.Closed, reason, detail));

        Assert.Empty(_heard);
    }

    [Fact]
    public void A_lost_connection_sounds_once_and_the_attempts_to_rebuild_it_do_not()
    {
        var shell = Shell(new AppSettings { AutoReconnect = true });
        var first = Open(shell);
        first.Set(new ConnectionStatus(ConnectionState.Connected));

        first.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));
        Assert.Equal([SoundEvent.ConnectionLost], Heard);

        // Der Server bleibt weg: Jeder Versuch der Sitzung scheitert – ohne weiteren Ton, auch lange danach.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            _clock += TimeSpan.FromMinutes(1);
            _scheduled[^1]();
            Assert.NotSame(first, _backend.Last);
            _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectFailed, "connection refused"));
        }

        Assert.Equal([SoundEvent.ConnectionLost], Heard);
    }

    [Fact]
    public void The_same_sound_comes_at_most_every_few_seconds()
    {
        var shell = Shell();
        for (var server = 0; server < 5; server++)
        {
            Open(shell).Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectFailed));
        }

        Assert.Single(_heard);

        _clock += ShellViewModel.SoundPause;
        Open(shell).Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectFailed));
        Assert.Equal(2, _heard.Count);
    }

    [Fact]
    public void Switched_off_nothing_sounds_and_the_window_rule_goes_along()
    {
        var shell = Shell();
        shell.SoundConnectFailed = false;
        Open(shell).Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectFailed));
        Assert.Empty(_heard);
        Assert.False(shell.Settings.SoundConnectFailed);

        shell.SoundsOnlyInBackground = true;
        Open(shell).Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.AuthFailed));
        Assert.Equal([(SoundEvent.AuthFailed, true)], _heard);

        // Probehören: sofort, auch für Abgeschaltetes, auch mit dem Fenster vorn.
        shell.PreviewSound(SoundEvent.ConnectFailed);
        shell.PreviewSound(SoundEvent.ConnectFailed);
        Assert.Equal([(SoundEvent.ConnectFailed, false), (SoundEvent.ConnectFailed, false)], _heard.Skip(1));
    }

    [Fact]
    public void A_failed_transfer_sounds_a_paused_one_does_not_and_a_finished_one_only_when_chosen()
    {
        var shell = TestShell.Create(out var backend);
        var heard = new List<SoundEvent>();
        shell.SoundRequested += (sound, _) => heard.Add(sound);
        var queue = (MockTransferQueue)backend.Transfers;

        queue.Complete(queue.Add("a.bin", "web01", TransferDirection.Upload, 10));
        Assert.Empty(heard);

        queue.Fail(queue.Add("b.bin", "web01", TransferDirection.Upload, 10), ITransferQueue.PausedDetail);
        Assert.Empty(heard);

        queue.Fail(queue.Add("c.bin", "web01", TransferDirection.Upload, 10), "permission_denied: /root");
        Assert.Equal([SoundEvent.TransferFailed], heard);

        shell.SoundTransferDone = true;
        queue.Complete(queue.Add("d.bin", "web01", TransferDirection.Download, 10));
        Assert.Equal([SoundEvent.TransferFailed, SoundEvent.TransferDone], heard);
    }

    [Fact]
    public void Errors_play_the_error_sound_of_windows_and_good_news_the_gentle_one()
    {
        Assert.Equal(SoundTone.Error, SoundEvents.ToneOf(SoundEvent.ConnectFailed));
        Assert.Equal(SoundTone.Error, SoundEvents.ToneOf(SoundEvent.AuthFailed));
        Assert.Equal(SoundTone.Error, SoundEvents.ToneOf(SoundEvent.TransferFailed));
        Assert.Equal(SoundTone.Warning, SoundEvents.ToneOf(SoundEvent.ConnectionLost));
        Assert.Equal(SoundTone.Info, SoundEvents.ToneOf(SoundEvent.Connected));
        Assert.Equal(SoundTone.Info, SoundEvents.ToneOf(SoundEvent.TransferDone));
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
