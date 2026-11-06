using System.Text;
using NexusSSH.Core.Exchange;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Was ein Profil über seine Verbindung sagt (Adressen, Wartezeit, Komprimierung, ältere Verfahren, Wiederverbinden),
/// seine Notiz – und der Satz, mit dem ein gescheiterter Aufbau sagt, woran es lag.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ConnectionOptionsTests
{
    private static readonly ServerProfile Plain = new("p1", "web01", "10.0.1.10", 22, "ubuntu", "Production", []);

    public ConnectionOptionsTests() => L.Init(AppLanguage.English);

    [Fact]
    public void A_new_profile_asks_for_nothing_special()
    {
        var editor = new ProfileEditorViewModel { Host = "h", User = "u" };

        Assert.Equal(15, editor.ConnectTimeout);
        Assert.Null(editor.AddressFamily.Id);
        Assert.True(editor.AutoReconnect);
        Assert.False(editor.Compression);
        Assert.False(editor.LegacyAlgorithms);

        // Die übliche Wartezeit steht nicht im Profil.
        var profile = editor.ToProfile();
        Assert.Equal((null, null, false, false, false, string.Empty), (profile.AddressFamily, profile.ConnectTimeout, profile.Compression, profile.LegacyAlgorithms, profile.NoReconnect, profile.Notes));
    }

    [Fact]
    public void The_editor_writes_the_wishes_into_the_profile_and_reads_them_back()
    {
        var editor = new ProfileEditorViewModel { Host = "h", User = "u" };
        editor.AddressFamily = editor.AddressFamilies.Single(choice => choice.Id == "ipv6");
        editor.ConnectTimeout = 40;
        editor.AutoReconnect = false;
        editor.Compression = true;
        editor.LegacyAlgorithms = true;
        editor.Notes = "  Database of the shop.\nMaintenance: Tuesday 2 am  ";

        var profile = editor.ToProfile();
        Assert.Equal(("ipv6", 40, true, true, true), (profile.AddressFamily, profile.ConnectTimeout, profile.Compression, profile.LegacyAlgorithms, profile.NoReconnect));
        Assert.Equal("Database of the shop.\nMaintenance: Tuesday 2 am", profile.Notes);

        var again = new ProfileEditorViewModel(profile with { Id = "p9" });
        Assert.Equal(("ipv6", 40.0, false, true, true), (again.AddressFamily.Id, again.ConnectTimeout, again.AutoReconnect, again.Compression, again.LegacyAlgorithms));
        Assert.Equal(profile.Notes, again.Notes);

        // Telnet kennt weder Komprimierung noch Verfahren; eine serielle Schnittstelle auch keine Adressen.
        again.IsTelnet = true;
        var telnet = again.ToProfile();
        Assert.Equal((false, false, "ipv6", 40), (telnet.Compression, telnet.LegacyAlgorithms, telnet.AddressFamily, telnet.ConnectTimeout));
        again.IsTelnet = false;
        again.IsSerial = true;
        var serial = again.ToProfile();
        Assert.Equal((null, null), (serial.AddressFamily, serial.ConnectTimeout));
    }

    [Fact]
    public void The_core_gets_the_wishes_and_tells_what_went_wrong()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.save"] = """{"ok":true,"profile":{"id":"p1","name":"old","host":"h","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true,"notes":"switch in rack 4","no_reconnect":true,"address_family":"ipv4","connect_timeout":30,"compression":true,"legacy_algorithms":true}}""";
        using var backend = new Backend.CoreBackend(transport);

        var saved = backend.Save(new ServerProfile(string.Empty, "old", "h", 22, "u", string.Empty, [], Notes: "switch in rack 4", NoReconnect: true, AddressFamily: "ipv4", ConnectTimeout: 30, Compression: true, LegacyAlgorithms: true));

        Assert.Equal(("switch in rack 4", true, "ipv4", 30, true, true), (saved.Notes, saved.NoReconnect, saved.AddressFamily, saved.ConnectTimeout, saved.Compression, saved.LegacyAlgorithms));
        var sent = Assert.Single(transport.Sent("profiles.save")).GetProperty("profile");
        Assert.Equal("switch in rack 4", sent.GetProperty("notes").GetString());
        Assert.True(sent.GetProperty("no_reconnect").GetBoolean());
        Assert.Equal("ipv4", sent.GetProperty("address_family").GetString());
        Assert.Equal(30, sent.GetProperty("connect_timeout").GetInt32());
        Assert.True(sent.GetProperty("compression").GetBoolean());
        Assert.True(sent.GetProperty("legacy_algorithms").GetBoolean());

        // Das Ende einer Sitzung nennt, woran der Aufbau lag.
        var connection = backend.Connect(saved);
        connection.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"closed","reason":"connect_failed","message":"connection refused","trouble":"refused"}""");
        Assert.Equal((CloseReason.ConnectFailed, ConnectTrouble.Refused), (connection.Status.Reason, connection.Status.Trouble));
    }

    [Theory]
    [InlineData(ConnectTrouble.Name, "cannot be resolved")]
    [InlineData(ConnectTrouble.Family, "no address of the kind")]
    [InlineData(ConnectTrouble.Refused, "no service listens on this port")]
    [InlineData(ConnectTrouble.Timeout, "No answer from the server")]
    [InlineData(ConnectTrouble.Unreachable, "no route")]
    [InlineData(ConnectTrouble.Algorithms, "Allow older algorithms")]
    public void A_failed_connection_says_what_was_wrong_and_what_to_try(string trouble, string expected)
    {
        var backend = new ScriptedBackend(Plain);
        var terminal = new StringBuilder();
        var session = new SessionViewModel(Plain, backend, new ImmediateDispatcher(), new FakePrompts());
        session.Output += data => terminal.Append(Encoding.UTF8.GetString(data.Span));
        session.Start();

        backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectFailed, "detail from the core", Trouble: trouble));

        Assert.Contains(expected, session.StatusText);
        Assert.Contains("(detail from the core)", session.StatusText);
        Assert.Contains(expected, terminal.ToString());
    }

    [Fact]
    public void Without_a_known_cause_the_general_sentence_stays()
    {
        var backend = new ScriptedBackend(Plain);
        var session = new SessionViewModel(Plain, backend, new ImmediateDispatcher(), new FakePrompts());
        session.Start();

        backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectFailed, "something else", Trouble: "other"));
        Assert.Equal("Server not reachable. (something else)", session.StatusText);
        Assert.Null(ConnectTrouble.Sentence(null));
    }

    [Fact]
    public void A_profile_can_switch_reconnecting_off_for_its_server()
    {
        var quiet = Plain with { NoReconnect = true };
        var backend = new ScriptedBackend(quiet);
        var scheduled = 0;
        var retry = new SessionRetry(() => true, (_, _) =>
        {
            scheduled++;
            return new Nothing();
        });
        var session = new SessionViewModel(quiet, backend, new ImmediateDispatcher(), new FakePrompts(), retry: retry);
        session.Start();
        backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));

        backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));

        Assert.Equal(0, scheduled);
        Assert.True(session.IsClosed);
    }

    [Fact]
    public void An_export_carries_notes_and_wishes_but_never_the_older_algorithms()
    {
        var profile = Plain with { Notes = "rack 4", NoReconnect = true, AddressFamily = "ipv4", ConnectTimeout = 30, Compression = true, LegacyAlgorithms = true };
        var file = DataExchange.Create([profile], new AppSettings(), "0.21.0", DateTimeOffset.UnixEpoch, "/home/a");

        var exported = Assert.Single(file.Profiles!);
        Assert.Equal(("rack 4", true, "ipv4", 30, true), (exported.Notes, exported.NoReconnect, exported.AddressFamily, exported.ConnectTimeout, exported.Compression));
        Assert.DoesNotContain(typeof(ExportedProfile).GetProperties(), property => property.Name.Contains("Legacy", StringComparison.Ordinal));

        // Auf einem leeren Rechner kommt alles an – nur nicht, was die Verbindung schwächer macht.
        var fresh = Assert.Single(DataExchange.Plan(file, [], new AppSettings(), "/home/b", keyExists: _ => true).Profiles).Profile;
        Assert.Equal(("rack 4", true, "ipv4", 30, true, false), (fresh.Notes, fresh.NoReconnect, fresh.AddressFamily, fresh.ConnectTimeout, fresh.Compression, fresh.LegacyAlgorithms));

        // Wo es hier schon eingeschaltet ist, bleibt es; und dasselbe Profil gilt als unverändert.
        var planned = Assert.Single(DataExchange.Plan(file, [profile], new AppSettings(), "/home/a", keyExists: _ => true).Profiles);
        Assert.True(planned.Profile.LegacyAlgorithms);
        Assert.Equal(ProfileChange.Unchanged, planned.Change);

        // Eine geänderte Notiz ist eine Änderung.
        var edited = Assert.Single(DataExchange.Plan(file, [profile with { Notes = "rack 5" }], new AppSettings(), "/home/a", keyExists: _ => true).Profiles);
        Assert.Equal(ProfileChange.Changed, edited.Change);
    }

    [Fact]
    public void A_foreign_file_cannot_bring_in_nonsense()
    {
        var file = new ExportFile
        {
            Profiles =
            [
                new ExportedProfile("x", "n", "h", 22, "u", Notes: "a\u0007b\r\nc" + new string('x', DataExchange.MaxNotes), AddressFamily: "ipx", ConnectTimeout: 100_000),
            ],
        };

        var profile = Assert.Single(DataExchange.Plan(file, [], new AppSettings(), "/home/b", keyExists: _ => true).Profiles).Profile;

        Assert.StartsWith("ab\nc", profile.Notes);
        Assert.Equal(DataExchange.MaxNotes, profile.Notes.Length);
        Assert.Equal((null, null), (profile.AddressFamily, profile.ConnectTimeout));
    }

    [Fact]
    public void The_server_filter_finds_a_word_from_the_notes()
    {
        var shell = TestShell.Create(out _);
        var first = shell.Profiles[0];
        shell.SaveProfile(first with { Notes = "Ansprechpartner: Frau Zimmermann" });

        shell.HomeFilter = "zimmermann";

        Assert.Equal(1, shell.HomeGroups.Sum(group => group.Count));
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
