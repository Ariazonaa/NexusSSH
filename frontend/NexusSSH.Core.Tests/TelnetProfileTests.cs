using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Telnet-Profile: für Geräte ohne SSH, mit weniger Angaben und ohne alles, was SSH voraussetzt.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TelnetProfileTests
{
    public TelnetProfileTests() => L.Init(AppLanguage.English);

    [Fact]
    public void A_telnet_profile_needs_no_user_and_switches_to_its_usual_port()
    {
        var editor = new ProfileEditorViewModel { Host = "10.0.0.2" };
        Assert.False(editor.IsValid);

        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        editor.IsTelnet = true;

        Assert.True(editor.IsValid);
        Assert.Contains(nameof(editor.IsValid), changed);
        Assert.Equal(23, editor.Port);

        var profile = editor.ToProfile();
        Assert.Equal((ServerProfile.Telnet, true, string.Empty, 23), (profile.Protocol, profile.IsTelnet, profile.User, profile.Port));
        Assert.Equal("telnet://10.0.0.2", profile.Address);
        Assert.Equal("telnet://10.0.0.2:2323", (profile with { Port = 2323 }).Address);

        // Zurück zu SSH: wieder der übliche Port, und ohne Benutzer geht es nicht.
        editor.IsTelnet = false;
        Assert.Equal(22, editor.Port);
        Assert.False(editor.IsValid);
        Assert.Null(editor.ToProfile().Protocol);

        // Ein eigener Port bleibt beim Wechsel stehen.
        editor.Port = 2222;
        editor.IsTelnet = true;
        Assert.Equal(2222, editor.Port);
    }

    [Fact]
    public void A_telnet_profile_opens_as_one_and_takes_no_identity()
    {
        Settings.Identity[] identities = [new("i1", "Deploy", "deploy")];
        var stored = new ServerProfile("p1", "switch", "10.0.0.2", 23, string.Empty, string.Empty, [], Protocol: ServerProfile.Telnet);

        var editor = new ProfileEditorViewModel(stored, [stored], identities);

        Assert.True(editor.IsTelnet);
        Assert.Equal(23, editor.Port);

        editor.Identity = editor.IdentityChoices[1];
        Assert.Null(editor.ToProfile().IdentityId);
    }

    [Fact]
    public void The_core_is_told_the_protocol_and_keys_are_not_offered_for_telnet()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.save"] = """{"ok":true,"profile":{"id":"p1","name":"switch","host":"10.0.0.2","port":23,"user":"","group":"","tags":[],"favorite":false,"use_agent":true,"protocol":"telnet"}}""";
        using var backend = new Backend.CoreBackend(transport);

        var saved = backend.Save(new ServerProfile(string.Empty, "switch", "10.0.0.2", 23, string.Empty, string.Empty, [], Protocol: ServerProfile.Telnet));

        Assert.True(saved.IsTelnet);
        Assert.Equal("telnet", Assert.Single(transport.Sent("profiles.save")).GetProperty("profile").GetProperty("protocol").GetString());

        // Ein SSH-Profil nennt kein Protokoll.
        _ = backend.Save(new ServerProfile(string.Empty, "web", "h", 22, "u", string.Empty, []));
        Assert.False(transport.Sent("profiles.save").Last().GetProperty("profile").TryGetProperty("protocol", out _));

        var shell = TestShell.Create(out _);
        var session = shell.OpenSession(shell.Profiles[0] with { Protocol = ServerProfile.Telnet });
        session.Start();
        Assert.False(shell.RunCommand("session.createKey"));
    }
}

/// <summary>X11-Forwarding im Profil: wie Agent Forwarding eine Erlaubnis, die nie von selbst gesetzt ist.</summary>
[Collection(LanguageCollection.Name)]
public sealed class X11ForwardingTests
{
    public X11ForwardingTests() => L.Init(AppLanguage.English);

    [Fact]
    public void X11_forwarding_is_off_until_the_profile_allows_it_and_never_goes_with_telnet()
    {
        var editor = new ProfileEditorViewModel { Host = "h", User = "u" };
        Assert.False(editor.ToProfile().ForwardX11);

        editor.ForwardX11 = true;
        Assert.True(editor.ToProfile().ForwardX11);

        // Ein gespeichertes Profil öffnet mit seiner Erlaubnis.
        Assert.True(new ProfileEditorViewModel(editor.ToProfile() with { Id = "p1" }).ForwardX11);

        editor.IsTelnet = true;
        Assert.False(editor.ToProfile().ForwardX11);
    }

    [Fact]
    public void The_core_is_told_and_an_export_file_cannot_switch_it_on()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.save"] = """{"ok":true,"profile":{"id":"p1","name":"web","host":"h","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true,"forward_x11":true}}""";
        using var backend = new Backend.CoreBackend(transport);

        var saved = backend.Save(new ServerProfile(string.Empty, "web", "h", 22, "u", string.Empty, [], ForwardX11: true));

        Assert.True(saved.ForwardX11);
        Assert.True(Assert.Single(transport.Sent("profiles.save")).GetProperty("profile").GetProperty("forward_x11").GetBoolean());

        // Die Export-Datei kennt die Erlaubnis nicht: Sie wandert nicht mit und kommt nicht von außen herein.
        Assert.DoesNotContain(typeof(Exchange.ExportedProfile).GetProperties(), property => property.Name.Contains("X11", StringComparison.Ordinal));
    }
}

/// <summary>Serielle Profile: eine Schnittstelle dieses Rechners statt eines Servers im Netz.</summary>
[Collection(LanguageCollection.Name)]
public sealed class SerialProfileTests
{
    public SerialProfileTests() => L.Init(AppLanguage.English);

    [Fact]
    public void A_serial_profile_names_a_com_port_and_a_speed()
    {
        var editor = new ProfileEditorViewModel { Host = "COM3", IsTelnet = true };

        editor.IsSerial = true;

        // Seriell und Telnet schließen einander aus; einen Benutzer braucht es nicht.
        Assert.False(editor.IsTelnet);
        Assert.False(editor.IsNetwork);
        Assert.True(editor.IsValid);

        var profile = editor.ToProfile();
        Assert.Equal((ServerProfile.Serial, true, false, 9600), (profile.Protocol, profile.IsSerial, profile.IsSsh, profile.Baud));
        Assert.Equal("COM3, 9600 Baud", profile.Address);

        editor.Baud = 115200;
        Assert.Equal("COM3, 115200 Baud", editor.ToProfile().Address);

        // Was keine Schnittstelle ist, sagt das Feld gleich – auch kein anderes Gerät des Rechners.
        foreach (var name in new[] { "example.org", "COM", "COM0", "COM1234", "PhysicalDrive0", @"\.\COM1", "/dev/ttyUSB0" })
        {
            editor.Host = name;
            Assert.False(editor.IsValid, name);
            Assert.Equal("A serial port is called COM and a number, for example COM3.", editor.HostError);
        }

        editor.Host = "com12";
        Assert.True(editor.IsValid);
        editor.Baud = 7;
        Assert.False(editor.IsValid);
        Assert.Equal("The speed is a whole number between 50 and 4000000.", editor.BaudError);

        // Zurück zu Telnet oder SSH: Die Geschwindigkeit gehört nicht mehr dazu.
        editor.IsTelnet = true;
        Assert.False(editor.IsSerial);
        Assert.Null(editor.ToProfile().Baud);
    }

    // Ein Dispatcher wie der der Oberfläche: Was gepostet wird, läuft erst später – die Ausgabe kommt direkt.
    private sealed class LaterDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _posted = new();

        public void Post(Action action) => _posted.Enqueue(action);

        public void Run()
        {
            while (_posted.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    [Fact]
    public void The_line_about_connecting_stands_before_the_greeting_of_a_device_that_answers_at_once()
    {
        L.Init(AppLanguage.English);
        var profile = new ServerProfile("p1", "switch", "10.0.0.2", 23, string.Empty, string.Empty, [], Protocol: ServerProfile.Telnet);
        var backend = new ScriptedBackend(profile);
        var later = new LaterDispatcher();
        var session = new SessionViewModel(profile, backend, later, new FakePrompts());
        var terminal = new System.Text.StringBuilder();
        session.Output += data => terminal.Append(System.Text.Encoding.UTF8.GetString(data.Span));

        session.Start();
        var connection = backend.Last;

        // Der Kern meldet „verbindet“ (über den Dispatcher) – und das Gerät grüßt, bevor die Oberfläche dazu kommt.
        connection.Set(new ConnectionStatus(ConnectionState.Connecting));
        connection.Emit("Welcome to the switch\r\nlogin: ");
        later.Run();

        var text = terminal.ToString();
        var line = text.IndexOf("Connecting to telnet://10.0.0.2", StringComparison.Ordinal);
        Assert.True(line >= 0 && line < text.IndexOf("Welcome to the switch", StringComparison.Ordinal), text);

        // Und sie steht nur einmal da.
        Assert.Equal(line, text.LastIndexOf("Connecting to telnet://10.0.0.2", StringComparison.Ordinal));

        // Ein neuer Versuch kündigt sich wieder an.
        connection.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));
        later.Run();
        session.Reconnect();
        Assert.True(terminal.ToString().LastIndexOf("Connecting to telnet://10.0.0.2", StringComparison.Ordinal) > line);
    }

    [Fact]
    public void A_serial_profile_opens_as_one_goes_to_the_core_and_travels_with_an_export()
    {
        var stored = new ServerProfile("p1", "console", "COM4", 22, string.Empty, string.Empty, [], Protocol: ServerProfile.Serial, Baud: 115200);

        var editor = new ProfileEditorViewModel(stored, [stored]);
        Assert.True(editor.IsSerial);
        Assert.Equal(115200, editor.Baud);

        var transport = new FakeTransport();
        transport.Responses["profiles.save"] = """{"ok":true,"profile":{"id":"p1","name":"console","host":"COM4","port":22,"user":"","group":"","tags":[],"favorite":false,"use_agent":true,"protocol":"serial","baud":115200}}""";
        using var backend = new Backend.CoreBackend(transport);
        var saved = backend.Save(stored);
        Assert.Equal((true, 115200), (saved.IsSerial, saved.Baud));
        var sent = Assert.Single(transport.Sent("profiles.save")).GetProperty("profile");
        Assert.Equal(("serial", 115200), (sent.GetProperty("protocol").GetString(), sent.GetProperty("baud").GetInt32()));

        var file = Exchange.DataExchange.Create([stored], new Settings.AppSettings(), "1.0", DateTimeOffset.UnixEpoch, null);
        var plan = Exchange.DataExchange.Plan(Exchange.DataExchange.Parse(Exchange.DataExchange.Serialize(file)), [], new Settings.AppSettings(), null, _ => true);
        var planned = Assert.Single(plan.Profiles).Profile;
        Assert.Equal((ServerProfile.Serial, 115200, "COM4"), (planned.Protocol, planned.Baud, planned.Host));

        // Für eine serielle Sitzung gibt es keinen Schlüssel zu hinterlegen.
        var shell = TestShell.Create(out _);
        var session = shell.OpenSession(stored);
        session.Start();
        Assert.False(shell.RunCommand("session.createKey"));
    }
}
