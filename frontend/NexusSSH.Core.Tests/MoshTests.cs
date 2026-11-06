using NexusSSH.Core.Backend;
using NexusSSH.Core.Exchange;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Mosh in der App: die Option im Profil (und was sie abschaltet), der Weg durch Export und Protokoll, der Hinweis bei
/// Stille und die Dateien, die mit der Verbindung weggehen und von selbst wiederkommen. Die JSON-Texte sind dieselben
/// wie in <c>backend/nexus-core/src/protocol.rs</c>.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class MoshTests : IDisposable
{
    private const string Plain =
        """{"id":"p1","name":"web01","host":"10.0.1.10","port":22,"user":"ubuntu","group":"","tags":[],"favorite":false,"use_agent":true}""";

    private const string OverMosh =
        """{"id":"p2","name":"edge","host":"10.0.1.11","port":22,"user":"ubuntu","group":"","tags":[],"favorite":false,"use_agent":true,"mosh":true,"mosh_ports":"60001:60010"}""";

    private static readonly string Home = LocalPath.Of("Users", "anna");

    public MoshTests() => L.Init(AppLanguage.English);

    public void Dispose() => L.Init(AppLanguage.English);

    private static (ShellViewModel Shell, FakeTransport Transport, CoreBackend Backend) Create(AppSettings? settings = null)
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Plain}},{{OverMosh}}]}""";
        var backend = new CoreBackend(transport);
        return (new ShellViewModel(backend, new ImmediateDispatcher(), settings ?? new AppSettings(), new FakePrompts()), transport, backend);
    }

    // Öffnet den Tab zum Profil und lässt den Kern „verbunden“ melden; Mosh meldet sich erst danach.
    private static (SessionViewModel Session, ulong Number) Connected(ShellViewModel shell, FakeTransport transport, string id)
    {
        var session = shell.OpenSession(shell.Profiles.First(profile => profile.Id == id));
        session.Start();
        var number = transport.Sent("session.open").Last().GetProperty("session").GetUInt64();
        transport.Raise($$"""{"event":"session.state","session":{{number}},"state":"connected","home":"/home/ubuntu"}""");
        return (session, number);
    }

    [Fact]
    public void Mosh_is_offered_for_ssh_and_switches_off_what_it_cannot_carry()
    {
        var editor = new ProfileEditorViewModel(null)
        {
            Host = "h",
            User = "u",
            ForwardAgent = true,
            ForwardX11 = true,
            CharsetIndex = 1,
        };
        Assert.True(editor.CanUseMosh);
        Assert.True(editor.CanForwardAgent);

        // Mit Mosh gibt es weder Agent noch X11 noch einen anderen Zeichensatz: Die Felder gehen aus und sind aus.
        editor.Mosh = true;
        Assert.False(editor.CanForwardAgent);
        Assert.False(editor.CanChooseCharset);
        Assert.False(editor.ForwardAgent);
        Assert.False(editor.ForwardX11);
        Assert.Equal(0, editor.CharsetIndex);

        editor.MoshPorts = "  60001:60010 ";
        var profile = editor.ToProfile();
        Assert.True(profile.Mosh);
        Assert.Equal("60001:60010", profile.MoshPorts);
        Assert.False(profile.ForwardAgent);
        Assert.False(profile.ForwardX11);
        Assert.Null(profile.Charset);

        // Ohne Mosh sind die Felder wieder frei, und die Ports bleiben nicht zurück.
        editor.Mosh = false;
        Assert.True(editor.CanForwardAgent);
        Assert.True(editor.CanChooseCharset);
        Assert.False(editor.ToProfile().Mosh);
        Assert.Null(editor.ToProfile().MoshPorts);

        // Ein Profil, das Mosh trägt, zeigt es beim Öffnen.
        var again = new ProfileEditorViewModel(profile with { Id = "p9" });
        Assert.True(again.Mosh);
        Assert.Equal("60001:60010", again.MoshPorts);
        Assert.False(again.CanForwardAgent);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("60001", true)]
    [InlineData("60001:60010", true)]
    [InlineData("1:65535", true)]
    [InlineData("60010:60001", false)]
    [InlineData("0", false)]
    [InlineData("65536", false)]
    [InlineData("60001:", false)]
    [InlineData(":60010", false)]
    [InlineData("60001-60010", false)]
    [InlineData("6000x", false)]
    [InlineData("+60001", false)]
    [InlineData("60001:60010:60020", false)]
    [InlineData("060001", false)]
    public void The_ports_for_mosh_are_one_number_or_a_range_like_in_the_core(string ports, bool valid)
    {
        Assert.Equal(valid, MoshPorts.IsValid(ports));

        var editor = new ProfileEditorViewModel(null) { Host = "h", User = "u", Mosh = true, MoshPorts = ports };
        Assert.Equal(valid, editor.IsValid);
        Assert.Equal(valid, editor.MoshPortsError.Length == 0);

        // Ohne Mosh steht das Feld nicht da, und was darin steht, hindert das Speichern nicht.
        editor.Mosh = false;
        Assert.True(editor.IsValid);
    }

    [Fact]
    public void Mosh_is_for_ssh_only()
    {
        var editor = new ProfileEditorViewModel(null) { Host = "h", User = "u", Mosh = true, MoshPorts = "60001" };

        editor.IsTelnet = true;

        Assert.False(editor.CanUseMosh);
        var profile = editor.ToProfile();
        Assert.False(profile.Mosh);
        Assert.Null(profile.MoshPorts);

        editor.IsTelnet = false;
        editor.IsFtp = true;
        Assert.False(editor.CanUseMosh);
        Assert.False(editor.ToProfile().Mosh);
    }

    [Fact]
    public void A_profile_with_a_jump_host_cannot_use_mosh()
    {
        var jump = new ServerProfile("j1", "bastion", "bastion.example.org", 22, "jump", string.Empty, []);
        var editor = new ProfileEditorViewModel(null, [jump]) { Host = "h", User = "u" };
        Assert.True(editor.CanUseMosh);

        // UDP geht nicht durch einen Sprung-Server: Wer einen wählt, hat Mosh danach nicht mehr …
        editor.Mosh = true;
        editor.Jump = editor.JumpChoices.First(choice => choice.Id == "j1");
        Assert.False(editor.CanUseMosh);
        Assert.False(editor.Mosh);
        Assert.False(editor.ToProfile().Mosh);

        // … und kann es mit Sprung-Server auch nicht wählen: Gespeichert wird nie beides.
        editor.Mosh = true;
        var profile = editor.ToProfile();
        Assert.Equal("j1", profile.JumpId);
        Assert.False(profile.Mosh);
        Assert.Null(profile.MoshPorts);

        editor.Jump = editor.JumpChoices.First(choice => choice.Id is null);
        Assert.True(editor.CanUseMosh);
    }

    [Fact]
    public void Mosh_and_its_ports_travel_with_an_export()
    {
        var edge = new ServerProfile("e1", "edge", "edge.example.org", 22, "ubuntu", string.Empty, [], Mosh: true, MoshPorts: "60001:60010");
        var file = DataExchange.Create([edge], new AppSettings(), "0.21.0", DateTimeOffset.FromUnixTimeSeconds(1_791_000_000), Home);
        var entry = Assert.Single(file.Profiles!);
        Assert.True(entry.Mosh);
        Assert.Equal("60001:60010", entry.MoshPorts);

        var plan = DataExchange.Plan(DataExchange.Parse(DataExchange.Serialize(file)), [], new AppSettings(), Home, keyExists: _ => true);

        var planned = Assert.Single(plan.Profiles).Profile;
        Assert.True(planned.Mosh);
        Assert.Equal("60001:60010", planned.MoshPorts);
    }

    [Fact]
    public void An_import_takes_mosh_only_where_the_profile_can_carry_it()
    {
        var file = DataExchange.Parse(
            """
            {
              "format": "nexusssh-export", "version": 1,
              "profiles": [
                { "id": "a", "name": "plain", "host": "a.example.org", "port": 22, "user": "root", "mosh": true, "moshPorts": "60001" },
                { "id": "b", "name": "behind", "host": "b.example.org", "port": 22, "user": "root", "mosh": true, "jump": "a" },
                { "id": "c", "name": "odd ports", "host": "c.example.org", "port": 22, "user": "root", "mosh": true, "moshPorts": "9;rm -rf" },
                { "id": "d", "name": "old box", "host": "d.example.org", "port": 23, "user": "root", "protocol": "telnet", "mosh": true, "moshPorts": "60001" },
                { "id": "e", "name": "files", "host": "e.example.org", "port": 21, "user": "root", "protocol": "ftp", "mosh": true }
              ]
            }
            """);

        var plan = DataExchange.Plan(file, [], new AppSettings(), Home, keyExists: _ => true);
        var profiles = plan.Profiles.Select(planned => planned.Profile).ToList();

        Assert.Equal([true, false, true, false, false], profiles.Select(profile => profile.Mosh));
        Assert.Equal(["60001", null, null, null, null], profiles.Select(profile => profile.MoshPorts));
    }

    [Fact]
    public void An_import_does_not_bring_back_forwarding_a_mosh_profile_cannot_have()
    {
        var here = new ServerProfile("a", "plain", "a.example.org", 22, "root", string.Empty, [], ForwardAgent: true);
        var file = DataExchange.Parse(
            """{ "format": "nexusssh-export", "version": 1, "profiles": [ { "id": "a", "name": "plain", "host": "a.example.org", "port": 22, "user": "root", "mosh": true } ] }""");

        var planned = Assert.Single(DataExchange.Plan(file, [here], new AppSettings(), Home, keyExists: _ => true).Profiles).Profile;

        Assert.True(planned.Mosh);
        Assert.False(planned.ForwardAgent);
    }

    [Fact]
    public void The_core_is_told_mosh_and_the_chosen_prediction()
    {
        var (shell, transport, backend) = Create(new AppSettings { MoshPredict = "never" });
        var edge = shell.Profiles.First(profile => profile.Id == "p2");
        Assert.True(edge.Mosh);
        Assert.Equal("60001:60010", edge.MoshPorts);

        // Gespeichert wird beides; sonst ginge Mosh beim ersten Speichern aus der App verloren.
        transport.Responses["profiles.save"] = $$"""{"ok":true,"profile":{{OverMosh}}}""";
        backend.Save(edge with { Id = string.Empty });
        var sent = Assert.Single(transport.Sent("profiles.save")).GetProperty("profile");
        Assert.True(sent.GetProperty("mosh").GetBoolean());
        Assert.Equal("60001:60010", sent.GetProperty("mosh_ports").GetString());

        // Die Vorhersage gilt für ein Profil mit Mosh; eines ohne bekommt sie nicht.
        shell.OpenSession(edge).Start();
        Assert.Equal("never", transport.Sent("session.open").Last().GetProperty("mosh_predict").GetString());

        shell.OpenSession(shell.Profiles.First(profile => profile.Id == "p1")).Start();
        Assert.False(transport.Sent("session.open").Last().TryGetProperty("mosh_predict", out _));
    }

    [Fact]
    public void The_usual_prediction_is_not_sent_and_a_wrong_setting_is_the_usual_one()
    {
        var (shell, transport, _) = Create();
        shell.OpenSession(shell.Profiles.First(profile => profile.Id == "p2")).Start();
        Assert.False(transport.Sent("session.open").Last().TryGetProperty("mosh_predict", out _));

        var settings = new AppSettings { MoshPredict = "sometimes" };
        settings.Sanitize();
        Assert.Equal(AppSettings.DefaultMoshPredict, settings.MoshPredict);

        foreach (var known in AppSettings.MoshPredictions)
        {
            settings.MoshPredict = known;
            settings.Sanitize();
            Assert.Equal(known, settings.MoshPredict);
        }
    }

    [Fact]
    public void Changing_the_prediction_in_the_settings_reaches_the_core_for_later_sessions()
    {
        var (shell, transport, _) = Create();

        shell.MoshPredict = "always";
        Assert.False(shell.IsDefaultSetting("MoshPredict"));
        Connected(shell, transport, "p2");
        Assert.Equal("always", transport.Sent("session.open").Last().GetProperty("mosh_predict").GetString());

        Assert.True(shell.ResetSetting("MoshPredict"));
        Assert.True(shell.IsDefaultSetting("MoshPredict"));
        shell.OpenSession(shell.Profiles.First(profile => profile.Id == "p2")).Start();
        Assert.False(transport.Sent("session.open").Last().TryGetProperty("mosh_predict", out _));
    }

    [Fact]
    public void The_prediction_is_a_setting_of_this_computer_and_not_for_an_export()
    {
        // Wie schnell Tippen am Bildschirm erscheint, hängt an der Leitung dieses Rechners: kein Wert für alle.
        Assert.DoesNotContain(typeof(ExportedSettings).GetProperties(), property => property.Name.Contains("Mosh", StringComparison.Ordinal));
    }

    [Fact]
    public void A_lost_link_takes_files_and_server_info_away_and_a_new_one_brings_them_back()
    {
        var (shell, transport, backend) = Create();
        var (session, number) = Connected(shell, transport, "p2");
        transport.Raise($$"""{"event":"session.mosh","session":{{number}},"state":"running"}""");
        Assert.True(session.IsMosh);
        Assert.True(session.Files.IsAvailable);
        Assert.NotNull(session.Connection.Files);
        Assert.NotNull(session.Inspector.Monitor);
        Assert.NotNull(backend.SessionFor("p2", 0));

        transport.Raise($$"""{"event":"session.link_lost","session":{{number}}}""");

        // Das Terminal läuft weiter; Dateien, Server-Info und Transfers haben keine Verbindung mehr.
        Assert.Equal(ConnectionState.Connected, session.State);
        Assert.True(session.IsMosh);
        Assert.True(session.IsLinkLost);
        Assert.False(session.Files.IsAvailable);
        Assert.Equal("Files and server details disconnected – they come back as soon as the server can be reached", session.Files.UnavailableText);
        Assert.Null(session.Connection.Files);
        Assert.Null(session.Connection.Monitor);
        Assert.Null(session.Inspector.Monitor);
        Assert.True(session.Inspector.IsUnavailable);
        Assert.Equal(session.Files.UnavailableText, session.Inspector.UnavailableText);
        Assert.Null(backend.SessionFor("p2", number));

        // Ein neuer Aufbau, der scheiterte, sagt es dahinter – als Text des Servers, gesäubert.
        transport.Raise($$"""{"event":"session.link_lost","session":{{number}},"message":"edge: sign-in refused\u001b[31m"}""");
        Assert.StartsWith("Files and server details disconnected", session.Files.UnavailableText);
        Assert.Contains("(edge: sign-in refused", session.Files.UnavailableText, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', session.Files.UnavailableText);

        // Die Verbindung steht wieder: Dateien und Messung gehen weiter, und das Verzeichnis wird neu gelesen.
        var listed = transport.Sent("sftp.list").Count();
        transport.Raise($$"""{"event":"session.linked","session":{{number}},"home":"/home/ubuntu"}""");

        Assert.False(session.IsLinkLost);
        Assert.True(session.Files.IsAvailable);
        Assert.NotNull(session.Connection.Files);
        Assert.NotNull(session.Connection.Monitor);
        Assert.NotNull(session.Inspector.Monitor);
        Assert.False(session.Inspector.IsUnavailable);
        Assert.Equal("/home/ubuntu", session.Files.CurrentPath);
        Assert.Equal(listed + 1, transport.Sent("sftp.list").Count());
        Assert.Equal(number, backend.SessionFor("p2", 0));
        Assert.Equal(ConnectionState.Connected, session.State);
    }

    [Fact]
    public async Task The_folder_is_the_same_after_the_files_come_back()
    {
        var (shell, transport, _) = Create();
        var (session, number) = Connected(shell, transport, "p2");
        HashSet<ulong> answered = [];
        async Task AnswerAll()
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            foreach (var request in transport.Sent("sftp.list").Select(list => list.GetProperty("request").GetUInt64()).Where(answered.Add).ToList())
            {
                transport.Raise($$"""{"event":"sftp.result","request":{{request}},"ok":true,"entries":[]}""");
            }
        }

        await AnswerAll();
        var moved = session.Files.NavigateToAsync("/var/log");
        await AnswerAll();
        await moved.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.Equal("/var/log", session.Files.CurrentPath);

        transport.Raise($$"""{"event":"session.link_lost","session":{{number}}}""");
        transport.Raise($$"""{"event":"session.linked","session":{{number}},"home":"/home/ubuntu"}""");

        // Nach dem Anhängen wird erst das Verzeichnis von vorher versucht.
        Assert.Equal("/var/log", transport.Sent("sftp.list").Last().GetProperty("path").GetString());
    }

    [Fact]
    public void The_files_can_be_asked_to_come_back_at_once()
    {
        var (shell, transport, _) = Create();
        var (session, number) = Connected(shell, transport, "p2");
        Assert.False(session.RelinkCommand.CanExecute(null));

        transport.Raise($$"""{"event":"session.link_lost","session":{{number}},"message":"edge: no route"}""");
        Assert.True(session.RelinkCommand.CanExecute(null));
        session.RelinkCommand.Execute(null);

        Assert.Equal(number, Assert.Single(transport.Sent("session.relink")).GetProperty("session").GetUInt64());

        transport.Raise($$"""{"event":"session.linked","session":{{number}},"home":"/home/ubuntu"}""");
        Assert.False(session.RelinkCommand.CanExecute(null));
    }

    [Fact]
    public void A_session_that_is_reached_again_does_not_take_a_dead_link_for_a_new_tab()
    {
        var (shell, transport, _) = Create();
        var (_, number) = Connected(shell, transport, "p2");
        transport.Raise($$"""{"event":"session.link_lost","session":{{number}}}""");

        shell.OpenSession(shell.Profiles.First(profile => profile.Id == "p2")).Start();

        Assert.False(transport.Sent("session.open").Last().TryGetProperty("share", out _));
    }

    [Fact]
    public void Silence_is_said_on_the_tab_and_goes_away_when_mosh_hears_again()
    {
        var (shell, transport, _) = Create();
        var (session, number) = Connected(shell, transport, "p2");

        // Solange der Kern nichts von Mosh sagt, läuft das Terminal über SSH.
        Assert.False(session.IsMosh);
        Assert.Equal(string.Empty, session.MoshNote);

        transport.Raise($$"""{"event":"session.mosh","session":{{number}},"state":"running"}""");
        Assert.True(session.IsMosh);
        Assert.Equal(string.Empty, session.MoshNote);

        transport.Raise($$"""{"event":"session.mosh","session":{{number}},"state":"silent","silent_for":12}""");
        Assert.Equal("No answer for 12 s – Mosh keeps trying", session.MoshNote);
        Assert.True(session.HasMoshNote);

        transport.Raise($$"""{"event":"session.mosh","session":{{number}},"state":"silent","silent_for":17}""");
        Assert.Equal("No answer for 17 s – Mosh keeps trying", session.MoshNote);

        transport.Raise($$"""{"event":"session.mosh","session":{{number}},"state":"running"}""");
        Assert.Equal(string.Empty, session.MoshNote);
        Assert.False(session.HasMoshNote);
        Assert.True(session.IsMosh);
    }

    [Fact]
    public void Mosh_and_its_note_end_with_the_connection()
    {
        var (shell, transport, _) = Create();
        var (session, number) = Connected(shell, transport, "p2");
        transport.Raise($$"""{"event":"session.mosh","session":{{number}},"state":"silent","silent_for":5}""");
        transport.Raise($$"""{"event":"session.link_lost","session":{{number}}}""");
        Assert.True(session.HasMoshNote);

        transport.Raise($$"""{"event":"session.state","session":{{number}},"state":"closed","reason":"connection_lost"}""");

        Assert.False(session.IsMosh);
        Assert.False(session.HasMoshNote);
        Assert.False(session.IsLinkLost);
        Assert.True(session.IsClosed);
    }

    [Fact]
    public void Events_of_a_replaced_connection_are_ignored()
    {
        var (shell, transport, _) = Create();
        var (session, first) = Connected(shell, transport, "p2");
        transport.Raise($$"""{"event":"session.state","session":{{first}},"state":"closed","reason":"connection_lost"}""");
        session.Reconnect();
        var second = transport.Sent("session.open").Last().GetProperty("session").GetUInt64();
        Assert.NotEqual(first, second);

        transport.Raise($$"""{"event":"session.mosh","session":{{first}},"state":"silent","silent_for":30}""");
        transport.Raise($$"""{"event":"session.link_lost","session":{{first}}}""");

        Assert.False(session.IsMosh);
        Assert.False(session.IsLinkLost);
    }

    [Theory]
    [InlineData(AppLanguage.German)]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.French)]
    [InlineData(AppLanguage.Spanish)]
    public void Each_mosh_warning_has_a_sentence_in_four_languages(AppLanguage language)
    {
        L.Init(language);
        var transport = new FakeTransport();
        using var backend = new CoreBackend(transport);
        List<string> warnings = [];
        backend.Warning += warnings.Add;
        string[] codes = ["mosh_unavailable", "mosh_locale", "mosh_blocked"];

        foreach (var code in codes)
        {
            transport.Raise($$"""{"event":"core.warning","code":"{{code}}","message":"web01\u001b[31m: no UDP packet from mosh-server arrived within 15 seconds; the terminal runs over SSH"}""");
        }

        Assert.Equal(codes.Length, warnings.Count);
        Assert.All(warnings, warning =>
        {
            // Der Satz in der Sprache des Benutzers, dahinter die englische Einzelheit des Kerns – gesäubert.
            Assert.DoesNotContain(codes, code => warning.StartsWith(code, StringComparison.Ordinal));
            Assert.Contains(" (web01", warning, StringComparison.Ordinal);
            Assert.Contains(": no UDP packet from mosh-server", warning, StringComparison.Ordinal);
            Assert.EndsWith("; the terminal runs over SSH)", warning, StringComparison.Ordinal);
            Assert.DoesNotContain('\u001b', warning);
        });
        Assert.Equal(codes.Length, warnings.Distinct().Count());
        Assert.All(warnings, warning => Assert.Contains("Mosh", warning, StringComparison.Ordinal));
    }

    [Fact]
    public void Each_new_text_of_mosh_is_different_in_every_language()
    {
        string[] keys = ["MoshSilent", "MoshLinkLost", "WarnMoshUnavailable", "WarnMoshLocale", "WarnMoshBlocked", "SettingMoshPredict", "ProfileMosh", "ProfileMoshPorts"];
        foreach (var key in keys)
        {
            List<string> texts = [];
            foreach (var language in new[] { AppLanguage.German, AppLanguage.English, AppLanguage.French, AppLanguage.Spanish })
            {
                L.Init(language);
                var text = L.Get(key, 12);
                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}: {key}");
                Assert.NotEqual(key, text);
                texts.Add(text);
            }

            // Der Name „Mosh“ und die Zahl kommen in jeder Sprache gleich vor – sonst ist ein Text nicht übersetzt.
            Assert.True(texts.Distinct().Count() >= 3, key);
        }
    }
}
