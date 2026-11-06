using System.Text;
using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Sprung-Server und Agent Forwarding (Spec §12) auf der C#-Seite: Profil, Editor, Protokoll, Anzeige.</summary>
[Collection(LanguageCollection.Name)]
public sealed class JumpHostTests
{
    private static readonly ServerProfile Bastion = new("b", "bastion", "bastion.example.org", 22, "ops", "", []);
    private static readonly ServerProfile Inner = new("i", "inner", "10.0.0.5", 22, "app", "", [], JumpId: "b");
    private static readonly ServerProfile Deep = new("d", "deep", "10.0.0.9", 22, "app", "", [], JumpId: "i");
    private static readonly ServerProfile Other = new("o", "other", "other.example.org", 2222, "me", "", []);
    private static readonly ServerProfile[] All = [Bastion, Inner, Deep, Other];

    [Fact]
    public void Jump_host_and_agent_forwarding_travel_in_the_profile()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.save"] =
            """{"ok":true,"profile":{"id":"i","name":"inner","host":"10.0.0.5","port":22,"user":"app","group":"","tags":[],"favorite":false,"use_agent":true,"jump":"b","forward_agent":true}}""";
        var backend = new CoreBackend(transport);

        var saved = backend.Save(Inner with { Id = "", ForwardAgent = true });

        var sent = Assert.Single(transport.Sent("profiles.save")).GetProperty("profile");
        Assert.Equal("b", sent.GetProperty("jump").GetString());
        Assert.True(sent.GetProperty("forward_agent").GetBoolean());
        Assert.Equal("b", saved.JumpId);
        Assert.True(saved.ForwardAgent);

        // Ohne Sprung-Server fehlt das Feld ganz – der Kern liest das als „direkt“.
        backend.Save(Bastion);
        Assert.False(transport.Sent("profiles.save").Last().GetProperty("profile").TryGetProperty("jump", out _));
    }

    [Fact]
    public void A_new_profile_connects_directly_and_never_forwards_the_agent_by_default()
    {
        TestShell.Create(out _);
        var editor = new ProfileEditorViewModel(null, All);

        Assert.Null(editor.Jump.Id);
        Assert.Equal("Connect directly", editor.Jump.Name);
        Assert.False(editor.ForwardAgent);
        Assert.Equal(["Connect directly", "bastion  (ops@bastion.example.org)", "deep  (app@10.0.0.9)", "inner  (app@10.0.0.5)", "other  (me@other.example.org:2222)"], editor.JumpChoices.Select(choice => choice.Name));

        editor.Host = "h";
        editor.User = "u";
        Assert.Null(editor.ToProfile().JumpId);
    }

    [Fact]
    public void The_choices_leave_out_the_profile_itself_and_everything_that_jumps_through_it()
    {
        TestShell.Create(out _);

        // deep springt über inner, inner über bastion: Als Sprung-Server von bastion gäben beide einen Kreis.
        var editor = new ProfileEditorViewModel(Bastion, All);

        Assert.Equal([null, "o"], editor.JumpChoices.Select(choice => choice.Id));
    }

    [Fact]
    public void Editing_keeps_and_changes_the_jump_host()
    {
        TestShell.Create(out _);
        var editor = new ProfileEditorViewModel(Inner, All);
        Assert.Equal("b", editor.Jump.Id);

        editor.Jump = editor.JumpChoices.Single(choice => choice.Id == "o");
        editor.ForwardAgent = true;

        var profile = editor.ToProfile();
        Assert.Equal("o", profile.JumpId);
        Assert.True(profile.ForwardAgent);
        Assert.Equal("i", profile.Id);
    }

    [Fact]
    public void A_jump_host_whose_profile_is_gone_stays_visible_instead_of_vanishing_on_save()
    {
        TestShell.Create(out _);
        var orphan = Inner with { JumpId = "gelöscht" };

        var editor = new ProfileEditorViewModel(orphan, All);

        Assert.Equal("(profile deleted)", editor.Jump.Name);
        Assert.Equal("gelöscht", editor.ToProfile().JumpId);
    }

    [Fact]
    public void The_hop_of_a_state_event_reaches_the_status()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[{"id":"i","name":"inner","host":"10.0.0.5","port":22,"user":"app","group":"","tags":[],"favorite":false,"use_agent":true,"jump":"b"}]}""";
        var backend = new CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();

        transport.Raise("""{"event":"session.state","session":1,"state":"connecting","hop":"ops@bastion.example.org"}""");
        Assert.Equal("ops@bastion.example.org", connection.Status.Hop);

        transport.Raise("""{"event":"session.state","session":1,"state":"connected"}""");
        Assert.Null(connection.Status.Hop);
    }

    [Fact]
    public void A_missing_jump_profile_ends_the_connection_before_it_starts()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[{"id":"i","name":"inner","host":"10.0.0.5","port":22,"user":"app","group":"","tags":[],"favorite":false,"use_agent":true,"jump":"weg"}]}""";
        transport.Responses["session.open"] = """{"ok":false,"error":"jump_missing","message":"the jump host profile no longer exists"}""";
        var backend = new CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);

        connection.Terminal.Start();

        Assert.Equal(ConnectionState.Closed, connection.Status.State);
        Assert.Equal(CloseReason.ConnectFailed, connection.Status.Reason);
        Assert.Equal("the jump host profile no longer exists", connection.Status.Detail);
    }

    [Fact]
    public void The_terminal_names_each_server_on_the_way()
    {
        TestShell.Create(out _);
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[{"id":"i","name":"inner","host":"10.0.0.5","port":22,"user":"app","group":"","tags":[],"favorite":false,"use_agent":true,"jump":"b"}]}""";
        var backend = new CoreBackend(transport);
        var session = new SessionViewModel(backend.GetProfiles()[0], backend, new ImmediateDispatcher(), new FakePrompts());
        var printed = new StringBuilder();
        session.Output += data => printed.Append(Encoding.UTF8.GetString(data.Span));
        session.Start();

        transport.Raise("""{"event":"session.state","session":1,"state":"connecting","hop":"ops@bastion.example.org"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"authenticating","hop":"ops@bastion.example.org"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"connecting","hop":"app@10.0.0.5"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"authenticating","hop":"app@10.0.0.5"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"connected"}""");

        var text = printed.ToString();
        Assert.Contains("Connecting to ops@bastion.example.org …", text);
        Assert.Contains("Connecting to app@10.0.0.5 …", text);
        Assert.True(text.IndexOf("bastion", StringComparison.Ordinal) < text.IndexOf("10.0.0.5", StringComparison.Ordinal));
        Assert.Equal(ConnectionState.Connected, session.State);
    }

    private const string Three =
        """{"ok":true,"profiles":[{"id":"b","name":"bastion","host":"bastion.example.org","port":22,"user":"ops","group":"","tags":[],"favorite":false,"use_agent":true},{"id":"i","name":"inner","host":"10.0.0.5","port":22,"user":"app","group":"","tags":[],"favorite":false,"use_agent":true,"jump":"b"},{"id":"d","name":"deep","host":"10.0.0.9","port":22,"user":"app","group":"","tags":[],"favorite":false,"use_agent":true,"jump":"i"}]}""";

    private static (SessionViewModel Session, FakeTransport Transport, StringBuilder Printed) OpenDeep()
    {
        L.Init(AppLanguage.English);
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = Three;
        var backend = new CoreBackend(transport);
        var shell = new ShellViewModel(backend, new ImmediateDispatcher(), new AppSettings(), new FakePrompts());
        var session = shell.OpenSession(backend.GetProfiles().Single(profile => profile.Id == "d"));
        var printed = new StringBuilder();
        session.Output += data => printed.Append(Encoding.UTF8.GetString(data.Span));
        session.Start();
        return (session, transport, printed);
    }

    [Fact]
    public void The_route_names_every_server_and_a_lost_connection_is_no_failed_step()
    {
        var (session, transport, printed) = OpenDeep();

        Assert.True(session.HasRoute);
        Assert.Equal(["bastion", "inner"], session.Via.Select(jump => jump.Name));
        Assert.Equal("This computer → bastion → inner → deep", session.RouteLine);
        Assert.Contains("Route: This computer → bastion → inner → deep", printed.ToString());

        transport.Raise("""{"event":"session.state","session":1,"state":"connecting","hop":"ops@bastion.example.org"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"authenticating","hop":"ops@bastion.example.org"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"connecting","hop":"app@10.0.0.5"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"authenticating","hop":"app@10.0.0.5"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"connecting","hop":"app@10.0.0.9"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"authenticating","hop":"app@10.0.0.9"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"connected"}""");

        // Reißt eine stehende Verbindung, ist kein Schritt gescheitert.
        transport.Raise("""{"event":"session.state","session":1,"state":"closed","reason":"connection_lost"}""");
        Assert.DoesNotContain("Failed at step", printed.ToString());
    }

    [Fact]
    public void A_failure_on_the_way_names_the_server_it_happened_at()
    {
        var (session, transport, printed) = OpenDeep();
        transport.Raise("""{"event":"session.state","session":1,"state":"connecting","hop":"ops@bastion.example.org"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"authenticating","hop":"ops@bastion.example.org"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"connecting","hop":"app@10.0.0.5"}""");

        transport.Raise("""{"event":"session.state","session":1,"state":"closed","reason":"connect_failed","message":"jump host app@10.0.0.5: timed out"}""");

        Assert.Contains("Failed at step 2 of 3: inner", printed.ToString());

        // Ein neuer Versuch beginnt vorn.
        session.Reconnect();
        transport.Raise("""{"event":"session.state","session":2,"state":"connecting","hop":"ops@bastion.example.org"}""");
        transport.Raise("""{"event":"session.state","session":2,"state":"closed","reason":"connect_failed","message":"timed out"}""");
        Assert.Contains("Failed at step 1 of 3: bastion", printed.ToString());
    }

    [Fact]
    public void A_server_the_core_names_differently_still_moves_the_route_on()
    {
        var (_, transport, printed) = OpenDeep();

        // Das Profil des Sprung-Servers wurde inzwischen geändert: Der Kern nennt eine andere Adresse.
        transport.Raise("""{"event":"session.state","session":1,"state":"connecting","hop":"root@elsewhere"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"authenticating","hop":"root@elsewhere"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"connecting","hop":"root@other"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"closed","reason":"connect_failed","message":"timed out"}""");

        Assert.Contains("Failed at step 2 of 3: inner", printed.ToString());
    }

    [Fact]
    public void Without_a_jump_host_there_is_no_route()
    {
        var shell = TestShell.Create(out var backend);
        var printed = new StringBuilder();
        var session = shell.OpenSession(backend.GetProfiles().First(profile => profile.JumpId is null));
        session.Output += data => printed.Append(Encoding.UTF8.GetString(data.Span));
        session.Start();

        Assert.False(session.HasRoute);
        Assert.DoesNotContain("Route:", printed.ToString());
    }
}
