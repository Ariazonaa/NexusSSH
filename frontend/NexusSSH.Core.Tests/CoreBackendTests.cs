using System.Text;
using NexusSSH.Core.Backend;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Das Protokoll zum Rust-Kern von der C#-Seite. Die JSON-Texte hier sind dieselben, die
/// <c>backend/nexus-core/src/protocol.rs</c> liest und schreibt – ändert sich eine Seite, muss dieser Test mit.
/// </summary>
public sealed class CoreBackendTests
{
    private const string Web01 =
        """{"id":"p1","name":"web01","host":"10.0.1.10","port":2222,"user":"ubuntu","group":"Production","tags":["web"],"favorite":true,"key_path":"C:\\keys\\id","use_agent":false,"last_connected":1790000000}""";

    private static (CoreBackend Backend, FakeTransport Transport) Create(string profiles = "[]")
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":{{profiles}}}""";
        return (new CoreBackend(transport), transport);
    }

    [Fact]
    public void Profiles_are_read_from_the_core_at_start()
    {
        var (backend, _) = Create($"[{Web01}]");

        var profile = Assert.Single(backend.GetProfiles());
        Assert.Equal("p1", profile.Id);
        Assert.Equal("ubuntu@10.0.1.10:2222", profile.Address);
        Assert.Equal(["web"], profile.Tags);
        Assert.True(profile.IsFavorite);
        Assert.Equal(@"C:\keys\id", profile.KeyPath);
        Assert.False(profile.UseAgent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), profile.LastConnected);
    }

    [Fact]
    public void Saving_sends_the_profile_in_snake_case_and_returns_what_the_core_stored()
    {
        var (backend, transport) = Create();
        transport.Responses["profiles.save"] = $$"""{"ok":true,"profile":{{Web01}}}""";
        var changes = 0;
        backend.Changed += () => changes++;

        var saved = backend.Save(new ServerProfile("", "web01", "10.0.1.10", 2222, "ubuntu", "Production", ["web"], true, @"C:\keys\id", false));

        var sent = Assert.Single(transport.Sent("profiles.save")).GetProperty("profile");
        Assert.Equal("", sent.GetProperty("id").GetString());
        Assert.Equal(2222, sent.GetProperty("port").GetInt32());
        Assert.True(sent.GetProperty("favorite").GetBoolean());
        Assert.Equal(@"C:\keys\id", sent.GetProperty("key_path").GetString());
        Assert.False(sent.GetProperty("use_agent").GetBoolean());
        Assert.False(sent.TryGetProperty("last_connected", out _));
        Assert.Equal("p1", saved.Id);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void A_rejected_profile_surfaces_as_an_exception_with_the_cores_code()
    {
        var (backend, transport) = Create();
        transport.Responses["profiles.save"] = """{"ok":false,"error":"invalid_profile","message":"host"}""";

        var error = Assert.Throws<BackendException>(() => backend.Save(new ServerProfile("", "x", "two words", 22, "u", "", [])));

        Assert.Equal("invalid_profile", error.Code);
        Assert.Equal("host", error.Message);
    }

    [Fact]
    public void Deleting_names_the_profile_and_reloads_the_list()
    {
        var (backend, transport) = Create($"[{Web01}]");
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[]}""";

        backend.Delete("p1");

        Assert.Equal("p1", Assert.Single(transport.Sent("profiles.delete")).GetProperty("id").GetString());
        Assert.Empty(backend.GetProfiles());
    }

    [Fact]
    public void A_connection_opens_only_when_the_terminal_starts_and_carries_its_size()
    {
        var (backend, transport) = Create($"[{Web01}]");
        var connection = backend.Connect(backend.GetProfiles()[0]);
        Assert.Equal(ConnectionState.Idle, connection.Status.State);
        Assert.Empty(transport.Sent("session.open"));

        connection.Terminal.Resize(132, 43);
        connection.Terminal.Start();
        connection.Terminal.Start();

        var open = Assert.Single(transport.Sent("session.open"));
        Assert.Equal("p1", open.GetProperty("profile_id").GetString());
        Assert.Equal(132, open.GetProperty("cols").GetInt32());
        Assert.Equal(43, open.GetProperty("rows").GetInt32());
        Assert.Empty(transport.Sent("session.resize"));

        connection.Terminal.Resize(100, 30);
        var resize = Assert.Single(transport.Sent("session.resize"));
        Assert.Equal(open.GetProperty("session").GetUInt64(), resize.GetProperty("session").GetUInt64());
        Assert.Equal(100, resize.GetProperty("cols").GetInt32());
    }

    [Fact]
    public void State_events_reach_only_their_own_connection()
    {
        var (backend, transport) = Create($"[{Web01}]");
        var first = backend.Connect(backend.GetProfiles()[0]);
        var second = backend.Connect(backend.GetProfiles()[0]);
        first.Terminal.Start();
        second.Terminal.Start();
        var seen = new List<ConnectionStatus>();
        first.StatusChanged += seen.Add;

        transport.Raise("""{"event":"session.state","session":1,"state":"connecting"}""");
        transport.Raise("""{"event":"session.state","session":2,"state":"connected"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"closed","reason":"remote","exit_status":3}""");

        Assert.Equal([ConnectionState.Connecting, ConnectionState.Closed], seen.Select(status => status.State));
        Assert.Equal(CloseReason.Remote, first.Status.Reason);
        Assert.Equal(3, first.Status.ExitStatus);
        Assert.Equal(ConnectionState.Connected, second.Status.State);
    }

    [Fact]
    public void A_session_without_a_shell_stays_connected_with_its_files()
    {
        var (backend, transport) = Create($"[{Web01}]");
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        List<ConnectionStatus> seen = [];
        connection.StatusChanged += seen.Add;

        // Vor „verbunden“ hat die Meldung nichts zu bedeuten.
        transport.Raise("""{"event":"session.files_only","session":1}""");
        Assert.Empty(seen);

        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/home/ubuntu"}""");
        var files = connection.Files;
        transport.Raise("""{"event":"session.files_only","session":1,"exit_status":1}""");

        Assert.Equal(new ConnectionStatus(ConnectionState.Connected, ExitStatus: 1, FilesOnly: true), seen[^1]);
        Assert.NotNull(files);
        Assert.Same(files, connection.Files);

        // Lehnt der Server die Shell ab, gibt es keinen Rückgabewert.
        var second = backend.Connect(backend.GetProfiles()[0]);
        second.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":2,"state":"connected","home":"/"}""");
        transport.Raise("""{"event":"session.files_only","session":2}""");
        Assert.Equal(new ConnectionStatus(ConnectionState.Connected, FilesOnly: true), second.Status);
    }

    [Theory]
    [InlineData("user", CloseReason.User)]
    [InlineData("connect_failed", CloseReason.ConnectFailed)]
    [InlineData("host_key_rejected", CloseReason.HostKeyRejected)]
    [InlineData("auth_failed", CloseReason.AuthFailed)]
    [InlineData("connection_lost", CloseReason.ConnectionLost)]
    [InlineData("unknown_profile", CloseReason.UnknownProfile)]
    [InlineData("something_new", CloseReason.ConnectionLost)]
    public void Close_reasons_are_mapped(string reason, CloseReason expected)
    {
        var (backend, transport) = Create($"[{Web01}]");
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();

        transport.Raise($$"""{"event":"session.state","session":1,"state":"closed","reason":"{{reason}}","message":"detail"}""");

        Assert.Equal(expected, connection.Status.Reason);
        Assert.Equal("detail", connection.Status.Detail);
    }

    [Fact]
    public void An_open_request_the_core_refuses_closes_the_connection()
    {
        var (backend, transport) = Create($"[{Web01}]");
        transport.Responses["session.open"] = """{"ok":false,"error":"unknown_profile","message":"p1"}""";
        var connection = backend.Connect(backend.GetProfiles()[0]);

        connection.Terminal.Start();

        Assert.Equal(new ConnectionStatus(ConnectionState.Closed, CloseReason.UnknownProfile, "p1"), connection.Status);
    }

    [Fact]
    public void Terminal_bytes_pass_through_in_both_directions()
    {
        var (backend, transport) = Create($"[{Web01}]");
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        var output = new StringBuilder();
        connection.Terminal.Output += data => output.Append(Encoding.UTF8.GetString(data.Span));

        transport.RaiseOutput(1, "hällo");
        transport.RaiseOutput(2, "for someone else");
        connection.Terminal.Write(Encoding.UTF8.GetBytes("ls\r"));

        Assert.Equal("hällo", output.ToString());
        Assert.Equal((1UL, "ls\r"), Assert.Single(transport.Written));
    }

    [Fact]
    public void A_host_key_prompt_is_answered_with_its_prompt_number()
    {
        var (backend, transport) = Create($"[{Web01}]");
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        ConnectionPrompt? asked = null;
        connection.PromptRequested += prompt => asked = prompt;

        transport.Raise(
            """{"event":"prompt.host_key","session":1,"prompt":7,"host":"10.0.1.10","port":2222,"algorithm":"ssh-ed25519","fingerprint":"SHA256:abc","changed":true,"known_hosts_file":"C:\\x\\known_hosts","known_hosts_line":4}""");

        var hostKey = Assert.IsType<ConnectionPrompt.HostKey>(asked);
        Assert.Equal(("10.0.1.10", 2222, "ssh-ed25519", "SHA256:abc"), (hostKey.Host, hostKey.Port, hostKey.Algorithm, hostKey.Fingerprint));
        Assert.True(hostKey.Changed);
        Assert.Equal((@"C:\x\known_hosts", 4), (hostKey.KnownHostsFile, hostKey.KnownHostsLine));

        hostKey.Answer(true);
        var answer = Assert.Single(transport.Sent("prompt.answer"));
        Assert.Equal(7UL, answer.GetProperty("prompt").GetUInt64());
        Assert.True(answer.GetProperty("accept").GetBoolean());

        // Ohne weitere Angaben: kein bisheriger Fingerabdruck, und der Eintrag gilt als der der App.
        Assert.Equal((null, false), (hostKey.PreviousFingerprint, hostKey.KnownHostsForeign));
    }

    [Fact]
    public void A_changed_host_key_shows_the_old_fingerprint_and_says_whose_file_holds_it()
    {
        var (backend, transport) = Create($"[{Web01}]");
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        ConnectionPrompt? asked = null;
        connection.PromptRequested += prompt => asked = prompt;

        transport.Raise(
            """{"event":"prompt.host_key","session":1,"prompt":8,"host":"h","port":22,"algorithm":"ssh-ed25519","fingerprint":"SHA256:new","changed":true,"known_hosts_file":"C:\\Users\\a\\.ssh\\known_hosts","known_hosts_line":12,"previous_fingerprint":"SHA256:old","known_hosts_foreign":true}""");

        var hostKey = Assert.IsType<ConnectionPrompt.HostKey>(asked);
        Assert.Equal(("SHA256:new", "SHA256:old", true), (hostKey.Fingerprint, hostKey.PreviousFingerprint, hostKey.KnownHostsForeign));
    }

    [Fact]
    public void A_secret_prompt_sends_values_or_nothing_at_all_on_cancel()
    {
        var (backend, transport) = Create($"[{Web01}]");
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        var asked = new List<ConnectionPrompt.Secret>();
        connection.PromptRequested += prompt => asked.Add((ConnectionPrompt.Secret)prompt);

        transport.Raise(
            """{"event":"prompt.secret","session":1,"prompt":8,"kind":"passphrase","user":"ubuntu","host":"10.0.1.10","key_path":"C:\\keys\\id","fields":[{"text":"","echo":false}],"can_save":true,"attempt":2}""");
        transport.Raise(
            """{"event":"prompt.secret","session":1,"prompt":9,"kind":"keyboard_interactive","user":"ubuntu","host":"10.0.1.10","name":"2FA","instructions":"Enter the code","fields":[{"text":"Code: ","echo":true}],"can_save":false,"attempt":1}""");

        Assert.Equal((SecretKind.Passphrase, @"C:\keys\id", 2, true), (asked[0].Kind, asked[0].KeyPath, asked[0].Attempt, asked[0].CanSave));
        Assert.Equal((SecretKind.KeyboardInteractive, "2FA", "Enter the code"), (asked[1].Kind, asked[1].Name, asked[1].Instructions));
        Assert.Equal(new SecretField("Code: ", true), Assert.Single(asked[1].Fields));

        asked[0].Answer(new SecretAnswer(["sesam"], Save: true));
        asked[1].Answer(null);

        var answers = transport.Sent("prompt.answer").ToList();
        Assert.Equal(8UL, answers[0].GetProperty("prompt").GetUInt64());
        Assert.Equal("sesam", answers[0].GetProperty("values")[0].GetString());
        Assert.True(answers[0].GetProperty("save").GetBoolean());

        // Abbruch: nur die Nummer – der Kern liest fehlende Werte als „abgebrochen“.
        Assert.Equal(9UL, answers[1].GetProperty("prompt").GetUInt64());
        Assert.False(answers[1].TryGetProperty("values", out _));
        Assert.False(answers[1].TryGetProperty("accept", out _));
    }

    [Fact]
    public async Task Logs_are_asked_for_by_choice_and_come_back_as_plain_text()
    {
        var (backend, transport) = Create($"[{Web01}]");
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/"}""");

        // Das Journal: Zeitraum als festes Wort, Schwere als Zahl, die Einheit als Name – nie eine Befehlszeile.
        var reading = connection.Monitor!.LogsAsync(new LogQuery(null, LogSince.Hours6, 3, "nginx.service", 99_999));
        var asked = Assert.Single(transport.Sent("logs.read"));
        Assert.Equal(("6h", 3, "nginx.service", 2000), (asked.GetProperty("since").GetString(), asked.GetProperty("priority").GetInt32(), asked.GetProperty("unit").GetString(), asked.GetProperty("lines").GetInt32()));
        Assert.False(asked.TryGetProperty("path", out _));

        transport.Raise($$"""{"event":"sftp.result","request":{{asked.GetProperty("request").GetUInt64()}},"ok":true,"limited":true,"logs":[{"time":1760000000,"priority":3,"source":"ssh\u001b[31md","message":"failed ‮evil"},{"message":"no time"},null,{"time":-5,"priority":42,"source":null,"message":"odd"},{"time":1,"priority":1}]}""");
        var log = await reading;

        Assert.True(log.Limited);
        Assert.Equal(3, log.Lines.Count);
        Assert.Equal((DateTimeOffset.FromUnixTimeSeconds(1_760_000_000), 3, UsageLevel.Critical), (log.Lines[0].Time, log.Lines[0].Priority, log.Lines[0].Level));
        Assert.DoesNotContain('\u001b', log.Lines[0].Source);
        Assert.DoesNotContain('‮', log.Lines[0].Message);
        Assert.Equal((null, null, string.Empty, "no time"), (log.Lines[1].Time, log.Lines[1].Priority, log.Lines[1].Source, log.Lines[1].Message));
        Assert.Equal((null, null, "odd"), (log.Lines[2].Time, log.Lines[2].Priority, log.Lines[2].Message));

        // Eine Datei: nur der Pfad und die Zahl der Zeilen – Zeitraum, Schwere und Einheit gelten ihr nicht.
        var file = connection.Monitor.LogsAsync(new LogQuery("/var/log/syslog", LogSince.Day, 4, "x.service", 200));
        var second = transport.Sent("logs.read").Last();
        Assert.Equal(("/var/log/syslog", 200), (second.GetProperty("path").GetString(), second.GetProperty("lines").GetInt32()));
        Assert.False(second.TryGetProperty("since", out _));
        Assert.False(second.TryGetProperty("priority", out _));
        Assert.False(second.TryGetProperty("unit", out _));

        transport.Raise($$"""{"event":"sftp.result","request":{{second.GetProperty("request").GetUInt64()}},"ok":false,"error":"no_journal","message":"the server has no journalctl"}""");
        Assert.Equal("no_journal", (await Assert.ThrowsAsync<RemoteFileException>(() => file)).Code);
    }

    [Fact]
    public void Disposing_a_started_connection_closes_the_session_and_mutes_its_events()
    {
        var (backend, transport) = Create($"[{Web01}]");
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        var changes = 0;
        connection.StatusChanged += _ => changes++;

        connection.Dispose();
        connection.Dispose();
        transport.Raise("""{"event":"session.state","session":1,"state":"closed","reason":"user"}""");

        Assert.Single(transport.Sent("session.close"));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Disposing_a_connection_that_never_started_sends_nothing()
    {
        var (backend, transport) = Create($"[{Web01}]");

        backend.Connect(backend.GetProfiles()[0]).Dispose();

        Assert.Empty(transport.Sent("session.close"));
    }

    [Fact]
    public void Core_events_about_profiles_and_warnings_are_forwarded()
    {
        var (backend, transport) = Create();
        var warnings = new List<string>();
        var changes = 0;
        backend.Warning += warnings.Add;
        backend.Changed += () => changes++;

        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Web01}}]}""";
        transport.Raise("""{"event":"profiles.changed"}""");
        transport.Raise("""{"event":"core.warning","code":"not_known_yet","message":"C:\\k: bad"}""");
        transport.Raise("this is not json");
        transport.Raise("""{"event":"something.new","session":1}""");

        Assert.Single(backend.GetProfiles());
        Assert.Equal(1, changes);
        // Ein Hinweis, den die App noch nicht kennt, erscheint, wie er kam.
        Assert.Equal(@"not_known_yet: C:\k: bad", Assert.Single(warnings));

        // Jeder Hinweis, den der Kern heute schickt, hat einen Satz in der Sprache des Benutzers; die Meldung des
        // Kerns steht gesäubert dahinter.
        var known = new[]
        {
            "profiles_unreadable", "secret_not_saved", "tls_trust_not_saved", "known_hosts_not_saved", "agent_unavailable",
            "x11_unavailable", "files_over_shell", "host_certificate_invalid", "host_key_revoked", "key_unreadable",
        };
        foreach (var code in known)
        {
            transport.Raise($$"""{"event":"core.warning","code":"{{code}}","message":"web01: detail\u001b[31m"}""");
        }

        Assert.Equal(known.Length + 1, warnings.Count);
        Assert.All(warnings.Skip(1), warning =>
        {
            Assert.Contains(" (web01: detail", warning, StringComparison.Ordinal);
            Assert.DoesNotContain('\u001b', warning);
            Assert.DoesNotContain(known, code => warning.StartsWith(code, StringComparison.Ordinal));
        });
        Assert.Equal(known.Length, warnings.Skip(1).Distinct().Count());
    }

    [Fact]
    public void Disposing_the_backend_disposes_the_transport()
    {
        var (backend, transport) = Create();

        backend.Dispose();

        Assert.True(transport.Disposed);
    }
}
