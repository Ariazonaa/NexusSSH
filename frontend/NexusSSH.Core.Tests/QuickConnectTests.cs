using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Schnellverbinden: <c>benutzer@host[:port]</c> in der Palette öffnet eine Sitzung ohne gespeicherten Server.</summary>
[Collection(LanguageCollection.Name)]
public sealed class QuickConnectTests
{
    public QuickConnectTests() => L.Init(AppLanguage.English);

    [Theory]
    [InlineData("root@example.org", "root", "example.org", 22, "root@example.org")]
    [InlineData("  deploy@10.0.0.7:2222 ", "deploy", "10.0.0.7", 2222, "deploy@10.0.0.7:2222")]
    [InlineData("me@host:22", "me", "host", 22, "me@host")]
    [InlineData("a.b-c_d@my-host_1.lan:65535", "a.b-c_d", "my-host_1.lan", 65535, "a.b-c_d@my-host_1.lan:65535")]
    // In Klammern auch ohne Port – sonst läse Parse den Namen anders zurück.
    [InlineData("root@[2001:db8::1]", "root", "2001:db8::1", 22, "root@[2001:db8::1]")]
    [InlineData("root@[2001:db8::1]:2222", "root", "2001:db8::1", 2222, "root@[2001:db8::1]:2222")]
    public void A_target_is_read_like_ssh_reads_it(string text, string user, string host, int port, string display)
    {
        var target = QuickTarget.Parse(text);

        Assert.Equal(new QuickTarget(user, host, port), target);
        Assert.Equal(display, target!.Display);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.org")]
    [InlineData("@example.org")]
    [InlineData("root@")]
    [InlineData("root@host:0")]
    [InlineData("root@host:70000")]
    [InlineData("root@host:22x")]
    [InlineData("root@host:")]
    [InlineData("ro ot@host")]
    [InlineData("root@ho st")]
    [InlineData("root@host;rm -rf /")]
    [InlineData("root@host/path")]
    [InlineData("root@-oProxyCommand=calc")]
    [InlineData("root@[not-an-address]")]
    [InlineData("root@[10.0.0.1]")]
    [InlineData("root@[2001:db8::1]22")]
    [InlineData("root@2001:db8::1")]
    [InlineData("a:b@host")]
    [InlineData("-l root@host -p 22")]
    public void What_is_not_a_plain_target_is_none(string text) => Assert.Null(QuickTarget.Parse(text));

    [Fact]
    public void A_typed_target_tops_the_palette_and_opens_a_session_without_a_saved_server()
    {
        var shell = TestShell.Create(out var backend);
        var before = backend.GetProfiles().Count;

        // Ein Ziel steht zuoberst – Enter nimmt den ersten Treffer. Mit > (nur Befehle) und für Suchwörter nicht.
        var first = shell.SearchPalette("deploy@build.example:2222")[0];
        Assert.Equal(("Connect to deploy@build.example:2222", "Quick connect – without a saved server"), (first.Title, first.Detail));
        Assert.DoesNotContain(shell.SearchPalette(">deploy@build.example"), item => item.Title.StartsWith("Connect to", StringComparison.Ordinal));
        Assert.DoesNotContain(shell.SearchPalette("build"), item => item.Title.StartsWith("Connect to", StringComparison.Ordinal));

        first.Run();
        var session = shell.ActiveSession!;
        Assert.Equal(("deploy@build.example:2222", "build.example", 2222, "deploy", true), (session.Profile.Name, session.Profile.Host, session.Profile.Port, session.Profile.User, session.Profile.IsTransient));
        Assert.True(session.IsQuick);
        Assert.True(shell.CanKeepQuick);

        // In keiner Liste: Startseite, Palette, Export und die Server für Tunnel kennen es nicht.
        Assert.Equal(before, backend.GetProfiles().Count);
        Assert.DoesNotContain(shell.Profiles, profile => profile.Host == "build.example");
        Assert.DoesNotContain(shell.TunnelServers, profile => profile.Host == "build.example");
        Assert.DoesNotContain(shell.SearchPalette(string.Empty), item => item.Kind == PaletteKind.Server && item.Title.Contains("build.example", StringComparison.Ordinal));

        // Mit dem Tab ist es weg.
        var id = session.Profile.Id;
        Assert.Contains(backend.AllProfiles, profile => profile.Id == id);
        shell.CloseSession(session);
        Assert.DoesNotContain(backend.AllProfiles, profile => profile.Id == id);
    }

    [Fact]
    public void Keeping_turns_the_connection_into_an_ordinary_server()
    {
        var shell = TestShell.Create(out var backend);
        var session = shell.QuickConnect("deploy@build.example")!;
        session.Start();
        var second = shell.OpenSession(session.Profile);
        second.IsQuick = true;

        Assert.True(shell.RunCommand("session.keep"));

        var kept = backend.GetProfiles().Single(profile => profile.Host == "build.example");
        Assert.Equal((session.Profile.Id, "deploy@build.example", false), (kept.Id, kept.Name, kept.IsTransient));
        Assert.Contains(shell.Profiles, profile => profile.Id == kept.Id);
        Assert.Equal((false, false, false), (session.IsQuick, second.IsQuick, shell.CanKeepQuick));
        Assert.False(shell.RunCommand("session.keep"));

        // Danach bleibt der Server, auch wenn alle Tabs dazu schließen.
        shell.CloseSession(session);
        shell.CloseSession(second);
        Assert.Contains(backend.GetProfiles(), profile => profile.Id == kept.Id);
    }

    [Fact]
    public void A_server_that_exists_opens_as_itself_and_two_tabs_share_one_transient_profile()
    {
        var shell = TestShell.Create(out var backend);
        var known = backend.GetProfiles()[0];

        // Derselbe Benutzer, Host und Port wie ein gespeicherter Server: Der öffnet sich, kein zweites Profil daneben.
        var session = shell.QuickConnect($"{known.User}@{known.Host.ToUpperInvariant()}")!;
        Assert.Equal((known.Id, false), (session.Profile.Id, session.IsQuick));

        // Was kein Ziel ist, öffnet nichts.
        Assert.Null(shell.QuickConnect("just some words"));

        // Zwei Tabs zum selben Schnellziel: Das Profil fällt erst mit dem letzten.
        var first = shell.QuickConnect("deploy@build.example")!;
        var again = shell.OpenSession(first.Profile);
        again.IsQuick = true;
        shell.CloseSession(first);
        Assert.Contains(backend.AllProfiles, profile => profile.Id == first.Profile.Id);
        shell.CloseSession(again);
        Assert.DoesNotContain(backend.AllProfiles, profile => profile.Id == first.Profile.Id);
    }

    [Fact]
    public void The_core_is_told_that_a_profile_is_transient_and_ordinary_profiles_do_not_mention_it()
    {
        var quick = new ServerProfile(string.Empty, "deploy@build.example", "build.example", 22, "deploy", string.Empty, [], IsTransient: true);

        Assert.Contains("\"transient\":true", CoreProtocol.Serialize(new RequestDto("profiles.save") { Profile = ProfileDto.From(quick) }));
        Assert.DoesNotContain("transient", CoreProtocol.Serialize(new RequestDto("profiles.save") { Profile = ProfileDto.From(quick with { IsTransient = false }) }));

        var answer = CoreProtocol.ParseResponse("{\"ok\":true,\"profile\":{\"id\":\"p9\",\"name\":\"n\",\"host\":\"h\",\"port\":22,\"user\":\"u\",\"group\":\"\",\"tags\":[],\"transient\":true}}");
        Assert.True(answer.Profile!.ToProfile().IsTransient);
    }
}
