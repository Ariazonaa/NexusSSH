using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Portweiterleitung: Jedes Profil trägt seine Tunnel; sie stehen, solange eine Verbindung zum Server steht.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TunnelTests
{
    public TunnelTests() => L.Init(AppLanguage.English);

    private static TunnelRow Row(ShellViewModel shell, string server, string tunnel)
        => shell.Tunnels.Single(row => row.Profile.Name == server && row.Tunnel.Id == tunnel);

    [Fact]
    public void Tunnels_stand_with_the_connection_and_end_with_it()
    {
        var shell = TestShell.Create(out var backend);
        var database = backend.GetProfiles().Single(profile => profile.Name == "database01");

        // Ohne Verbindung steht keiner, und der Schalter ist gesperrt.
        Assert.Equal(["PostgreSQL", "SOCKS proxy localhost:1080"], shell.Tunnels.Select(row => row.Name));
        Assert.All(shell.Tunnels, row => Assert.Equal((TunnelState.Stopped, false, "No connection to the server"), (row.State, row.CanSwitch, row.StateText)));
        Assert.False(shell.SwitchTunnel(Row(shell, "database01", "t2"), on: true));
        Assert.Empty(shell.SessionTunnels);

        var session = shell.OpenSession(database);
        session.Start();

        // Mit der Verbindung startet, was es soll – der andere wartet auf Zuruf.
        Assert.Equal((TunnelState.Listening, true, "Listening"), (Row(shell, "database01", "t1").State, Row(shell, "database01", "t1").CanSwitch, Row(shell, "database01", "t1").StateText));
        Assert.Equal((TunnelState.Stopped, true, "Off"), (Row(shell, "database01", "t2").State, Row(shell, "database01", "t2").CanSwitch, Row(shell, "database01", "t2").StateText));
        Assert.Equal(["t1", "t2"], shell.SessionTunnels.Select(row => row.Tunnel.Id));
        Assert.True(shell.HasSessionTunnels);
        Assert.Equal("PostgreSQL, database01, Listening", Row(shell, "database01", "t1").ToString());
        Assert.Equal("localhost:5432 → 127.0.0.1:5432", Row(shell, "database01", "t1").Route);

        Assert.True(shell.SwitchTunnel(Row(shell, "database01", "t2"), on: true));
        Assert.True(Row(shell, "database01", "t2").IsListening);
        Assert.True(shell.SwitchTunnel(Row(shell, "database01", "t1"), on: false));
        Assert.Equal(TunnelState.Stopped, Row(shell, "database01", "t1").State);

        // Ein anderer Tab: Der Inspector zeigt dessen Tunnel (hier keine), die Startseite weiter alle.
        var web = shell.OpenSession(backend.GetProfiles().Single(profile => profile.Name == "web01"));
        web.Start();
        Assert.Empty(shell.SessionTunnels);
        Assert.Equal(2, shell.Tunnels.Count);
        Assert.True(Row(shell, "database01", "t2").IsListening);

        // Mit der Sitzung enden die Tunnel.
        shell.CloseSession(session);
        Assert.All(shell.Tunnels, row => Assert.Equal((TunnelState.Stopped, false), (row.State, row.CanSwitch)));
    }

    [Fact]
    public void A_tunnel_is_added_changed_and_deleted_and_a_failure_is_a_problem()
    {
        var shell = TestShell.Create(out var backend);
        var web = backend.GetProfiles().Single(profile => profile.Name == "web01");
        var session = shell.OpenSession(web);
        session.Start();
        Assert.Contains(shell.TunnelServers, profile => profile.Name == "web01");

        // Neu, mit der Verbindung zu starten: Die Verbindung steht schon, also steht er gleich. Die Kennung vergibt
        // das Backend.
        Assert.True(shell.SaveTunnel(web, new ProfileTunnel(string.Empty, TunnelKind.Local, "Intranet", 8080, "intranet.example", 80)));
        var added = Row(shell, "web01", "t1");
        Assert.Equal(("Intranet", TunnelState.Listening), (added.Name, added.State));
        Assert.Equal("localhost:8080 → intranet.example:80", added.Route);

        // Ein belegter Port: Der Tunnel steht nicht, und das steht unter „Probleme“ – mit dem Grund.
        Assert.True(shell.SaveTunnel(web, new ProfileTunnel(string.Empty, TunnelKind.Dynamic, string.Empty, MockBackend.TakenTunnelPort)));
        var failed = Row(shell, "web01", "t2");
        Assert.Equal((TunnelState.Failed, true), (failed.State, failed.HasFailed));
        Assert.Equal("Not listening: port 9 is already in use on this computer", failed.StateText);
        Assert.Contains(shell.Problems, entry => entry.Text == "web01: the tunnel “SOCKS proxy localhost:9” is not listening (port 9 is already in use on this computer)");

        // Geändert: derselbe Tunnel an einem anderen Port – er wird angehalten und steht dann dort.
        Assert.True(shell.SaveTunnel(web, added.Tunnel with { ListenPort = 8081, Open = true }));
        var changed = Row(shell, "web01", "t1");
        Assert.Equal((8081, true, TunnelState.Listening), (changed.Tunnel.ListenPort, changed.IsOpen, changed.State));

        // Was nicht stehen kann, wird nicht gespeichert.
        Assert.False(shell.SaveTunnel(web, new ProfileTunnel(string.Empty, TunnelKind.Local, "kaputt", 0, "db", 5432)));
        Assert.False(shell.SaveTunnel(web, new ProfileTunnel(string.Empty, TunnelKind.Remote, "kaputt", 80, "a b", 80)));
        Assert.False(shell.SaveTunnel(web, new ProfileTunnel(string.Empty, TunnelKind.Local, "kaputt", 80, "db", 70000)));
        Assert.True(new ProfileTunnel(string.Empty, TunnelKind.Dynamic, string.Empty, 1080).IsValid);
        Assert.True(new ProfileTunnel(string.Empty, TunnelKind.Remote, string.Empty, 8022, "::1", 22).IsValid);

        // Gelöscht: Er endet und ist weg; der andere bleibt.
        Assert.True(shell.DeleteTunnel(changed));
        Assert.Equal(["t2"], shell.Tunnels.Where(row => row.Profile.Name == "web01").Select(row => row.Tunnel.Id));
        Assert.Equal(["t2"], backend.GetProfiles().Single(profile => profile.Name == "web01").Tunnels!.Select(tunnel => tunnel.Id));
        Assert.True(shell.DeleteTunnel(Row(shell, "web01", "t2")));
        Assert.Null(backend.GetProfiles().Single(profile => profile.Name == "web01").Tunnels);
    }

    [Fact]
    public void Editing_a_profile_keeps_its_tunnels()
    {
        var shell = TestShell.Create(out var backend);
        var database = backend.GetProfiles().Single(profile => profile.Name == "database01");

        var edited = new ProfileEditorViewModel(database, backend.GetProfiles()) { Name = "datenbank" }.ToProfile();

        Assert.Equal("datenbank", edited.Name);
        Assert.Equal(["t1", "t2"], edited.Tunnels!.Select(tunnel => tunnel.Id));
        Assert.NotNull(shell.SaveProfile(edited));
        Assert.Equal(2, shell.Tunnels.Count(row => row.Profile.Name == "datenbank"));
    }

    [Fact]
    public void The_core_is_told_tunnels_as_part_of_the_profile_and_reports_their_state()
    {
        var profile = new ServerProfile("p1", "db", "db.example", 22, "admin", string.Empty, [], Tunnels:
        [
            new ProfileTunnel("t1", TunnelKind.Local, "PostgreSQL", 5432, "127.0.0.1", 5432),
            new ProfileTunnel(string.Empty, TunnelKind.Remote, string.Empty, 8022, "::1", 22, Open: true, Auto: false),
            new ProfileTunnel("proxy", TunnelKind.Dynamic, string.Empty, 1080),
        ]);

        var json = CoreProtocol.Serialize(new RequestDto("profiles.save") { Profile = ProfileDto.From(profile) });

        Assert.Contains(
            "\"tunnels\":[{\"id\":\"t1\",\"kind\":\"local\",\"name\":\"PostgreSQL\",\"listen_port\":5432,\"target_host\":\"127.0.0.1\",\"target_port\":5432,\"open\":false,\"auto\":true},"
            + "{\"id\":\"\",\"kind\":\"remote\",\"name\":\"\",\"listen_port\":8022,\"target_host\":\"::1\",\"target_port\":22,\"open\":true,\"auto\":false},"
            + "{\"id\":\"proxy\",\"kind\":\"dynamic\",\"name\":\"\",\"listen_port\":1080,\"target_host\":\"\",\"target_port\":0,\"open\":false,\"auto\":true}]",
            json);

        // Ohne Tunnel steht davon nichts im Profil.
        Assert.DoesNotContain("tunnels", CoreProtocol.Serialize(new RequestDto("profiles.save") { Profile = ProfileDto.From(profile with { Tunnels = null }) }));

        Assert.Equal(
            "{\"op\":\"tunnel.set\",\"session\":7,\"tunnel\":\"t1\",\"on\":true}",
            CoreProtocol.Serialize(new RequestDto("tunnel.set") { Session = 7, Tunnel = "t1", On = true }));

        var answer = CoreProtocol.ParseResponse(
            "{\"ok\":true,\"profile\":{\"id\":\"p1\",\"name\":\"db\",\"host\":\"db.example\",\"port\":22,\"user\":\"admin\",\"group\":\"\",\"tags\":[],"
            + "\"tunnels\":[{\"id\":\"t7\",\"kind\":\"dynamic\",\"name\":\"\",\"listen_port\":1080,\"target_host\":\"\",\"target_port\":0,\"open\":false,\"auto\":false},"
            + "{\"id\":\"x\",\"kind\":\"something new\",\"listen_port\":1,\"target_host\":\"h\",\"target_port\":2,\"open\":false,\"auto\":true}]}}");
        var tunnels = answer.Profile!.ToProfile().Tunnels!;
        Assert.Equal(new ProfileTunnel("t7", TunnelKind.Dynamic, string.Empty, 1080, Auto: false), tunnels[0]);

        // Eine Art, die diese Fassung nicht kennt, gilt als lokal – der Kern prüft beim Speichern ohnehin.
        Assert.Equal(TunnelKind.Local, tunnels[1].Kind);

        var state = CoreProtocol.ParseEvent("{\"event\":\"tunnel.state\",\"profile\":\"p1\",\"tunnel\":\"t7\",\"state\":\"failed\",\"message\":\"port 1080 is already in use on this computer\"}");
        Assert.Equal(("tunnel.state", "p1", "t7", "failed"), (state!.Event, state.Profile, state.Tunnel, state.State));
    }

    [Fact]
    public void Switching_goes_to_a_standing_connection_of_the_server()
    {
        var profile = new ServerProfile("p1", "db", "db.example", 22, "admin", string.Empty, [], Tunnels: [new ProfileTunnel("t1", TunnelKind.Local, "PostgreSQL", 5432, "127.0.0.1", 5432)]);
        var backend = new ScriptedBackend(profile);
        var shell = TestShell.Create(backend);
        var session = shell.OpenSession(profile);
        session.Start();
        var row = shell.Tunnels.Single();

        // Noch im Aufbau: Es gibt nichts, worüber der Tunnel laufen könnte.
        Assert.False(shell.SwitchTunnel(row, on: true));
        Assert.Empty(backend.TunnelRequests);

        backend.Last.Set(new NexusSSH.Core.Services.ConnectionStatus(NexusSSH.Core.Services.ConnectionState.Connected));
        row = shell.Tunnels.Single();
        Assert.True(row.CanSwitch);
        Assert.True(shell.SwitchTunnel(row, on: true));
        Assert.Equal((backend.Last, "t1", true), backend.TunnelRequests.Single());

        // Was der Kern meldet, steht in der Zeile; eine Meldung zu einem Tunnel, den es nicht gibt, stört nicht.
        backend.Report(new TunnelNotice("p1", "t1", TunnelState.Listening));
        backend.Report(new TunnelNotice("p1", "gone", TunnelState.Failed, "whatever"));
        Assert.True(shell.Tunnels.Single().IsListening);
        Assert.Empty(shell.Problems);
    }
}
