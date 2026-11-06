using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Start mit Ziel: Kommandozeile, <c>ssh://</c>-Links und die Sprungliste – nur Ziele, nie mehr, und erst nach der Rückfrage.</summary>
[Collection(LanguageCollection.Name)]
public sealed class LaunchTests
{
    [Theory]
    [InlineData("ssh://deploy@build.example", "deploy", "build.example", 22)]
    [InlineData("SSH://deploy@build.example:2222/", "deploy", "build.example", 2222)]
    [InlineData("ssh://root@[2001:db8::1]:2200", "root", "2001:db8::1", 2200)]
    [InlineData("ssh://build.example", "", "build.example", 22)]
    [InlineData("ssh://build.example:2022", "", "build.example", 2022)]
    [InlineData("deploy@10.0.0.5:22", "deploy", "10.0.0.5", 22)]
    public void A_target_names_user_host_and_port(string text, string user, string host, int port)
    {
        var request = LaunchRequest.Parse(text);

        Assert.NotNull(request);
        Assert.Equal(new QuickTarget(user, host, port), request.Target);
        Assert.Equal(text.StartsWith("ssh", StringComparison.OrdinalIgnoreCase), request.FromLink);
    }

    [Theory]
    // Mehr als ein Ziel: ein Pfad, eine Abfrage, Parameter (ein Fingerabdruck, dem keiner glaubt), Kodiertes.
    [InlineData("ssh://deploy@build.example/etc/passwd")]
    [InlineData("ssh://deploy@build.example?command=reboot")]
    [InlineData("ssh://deploy@build.example#x")]
    [InlineData("ssh://deploy;fingerprint=SHA256-abc@build.example")]
    [InlineData("ssh://deploy%40x@build.example")]
    [InlineData("ssh://de ploy@build.example")]
    [InlineData("ssh://deploy@build.example:99999")]
    [InlineData("ssh://-oProxyCommand=calc@build.example")]
    [InlineData("ssh://deploy@-oProxyCommand=calc")]
    [InlineData("ssh://deploy@build.example\r\nx")]
    [InlineData("ssh://good.example＠x@evil.example")]
    [InlineData("ssh://dеploy@build.example")]
    [InlineData("ssh://")]
    [InlineData("ssh:///")]
    [InlineData("ssh://deploy@")]
    [InlineData("telnet://deploy@build.example")]
    [InlineData("https://build.example")]
    [InlineData("build.example")]
    public void Anything_beyond_a_target_is_no_target(string text)
        => Assert.Null(LaunchRequest.Parse(text));

    [Fact]
    public void The_switches_of_the_app_are_passed_over_and_two_targets_give_none()
    {
        Assert.Equal("build.example", LaunchRequest.FromArguments(["--no-activate", "deploy@build.example"])?.Target?.Host);
        Assert.Equal("p-17_a", LaunchRequest.FromArguments(["--open", "p-17_a"])?.ProfileId);
        Assert.Null(LaunchRequest.FromArguments(["--demo"]));
        Assert.Null(LaunchRequest.FromArguments(["a@one.example", "b@two.example"]));
        Assert.Null(LaunchRequest.FromArguments(["--open", "p1", "a@one.example"]));
        // Eine Kennung ist nur das, wonach sie aussieht.
        Assert.Null(LaunchRequest.FromArguments(["--open", "..\\..\\x"]));
        Assert.Null(LaunchRequest.FromArguments(["--open"]));
        // Hinter einem kaputten Link eingeschleust (ein Programm reichte den Link ohne Kodierung weiter): nichts.
        Assert.Null(LaunchRequest.FromArguments(["ssh://\"", "--open", "p1"]));
        Assert.Null(LaunchRequest.FromArguments(["ssh://a@b\"", "--open", "p1"]));
        // Ein Link steht allein – mit Schaltern daneben (aus einem `"` im Link entstanden) gilt nichts.
        Assert.Null(LaunchRequest.FromArguments(["ssh://a@b", "--no-activate"]));
        Assert.Null(LaunchRequest.FromArguments(["--demo", "ssh://a@b"]));
        Assert.NotNull(LaunchRequest.FromArguments(["ssh://a@b"]));
    }

    [Fact]
    public async Task An_ipv6_target_on_the_default_port_still_opens()
    {
        var shell = Shell(new FakePrompts { Confirm = true }, out _);

        Assert.Equal("root@[2001:db8::1]", LaunchRequest.Parse("ssh://root@[2001:db8::1]")!.Target!.Display);
        var session = await shell.OpenLaunchAsync(LaunchRequest.Parse("ssh://root@[2001:db8::1]")!);
        Assert.Equal(("2001:db8::1", 22), (session!.Profile.Host, session.Profile.Port));
    }

    [Fact]
    public async Task A_target_connects_only_after_the_user_agreed()
    {
        var prompts = new FakePrompts { Confirm = false };
        var shell = Shell(prompts, out _);

        Assert.Null(await shell.OpenLaunchAsync(LaunchRequest.Parse("ssh://deploy@build.example")!));
        Assert.Single(prompts.Confirmed);
        Assert.Empty(shell.Sessions);

        prompts.Confirm = true;
        var session = await shell.OpenLaunchAsync(LaunchRequest.Parse("ssh://deploy@build.example")!);
        Assert.NotNull(session);
        Assert.True(session.IsQuick);
        Assert.Equal(("deploy", "build.example", 22), (session.Profile.User, session.Profile.Host, session.Profile.Port));
    }

    [Fact]
    public async Task A_saved_server_opens_as_itself_and_a_link_without_user_takes_it_or_the_local_account()
    {
        var prompts = new FakePrompts { Confirm = true };
        var shell = Shell(prompts, out var backend);
        var known = backend.GetProfiles()[0];

        var same = await shell.OpenLaunchAsync(LaunchRequest.Parse($"ssh://{known.Host}:{known.Port}")!);
        Assert.Equal((known.Id, false), (same!.Profile.Id, same.IsQuick));

        shell.LocalUserName = "kim";
        var local = await shell.OpenLaunchAsync(LaunchRequest.Parse("ssh://elsewhere.example")!);
        Assert.Equal(("kim", true), (local!.Profile.User, local.IsQuick));

        // Ein Konto, dessen Name kein Benutzer sein kann: nichts, nur ein Eintrag in der Ausgabe.
        shell.LocalUserName = "Kim Doe";
        Assert.Null(await shell.OpenLaunchAsync(LaunchRequest.Parse("ssh://third.example")!));
    }

    [Fact]
    public async Task The_jump_list_opens_saved_servers_without_asking_and_lists_the_recent_ones()
    {
        var prompts = new FakePrompts { Confirm = false };
        var shell = Shell(prompts, out var backend);
        var profiles = backend.GetProfiles();

        var opened = await shell.OpenLaunchAsync(LaunchRequest.FromArguments([LaunchRequest.OpenSwitch, profiles[1].Id])!);
        Assert.Equal(profiles[1].Id, opened!.Profile.Id);
        Assert.Empty(prompts.Confirmed);
        Assert.Null(await shell.OpenLaunchAsync(LaunchRequest.FromArguments([LaunchRequest.OpenSwitch, "gone"])!));

        var now = DateTimeOffset.Now;
        shell.SaveProfile(profiles[0] with { LastConnected = now.AddHours(-2) });
        shell.SaveProfile(profiles[2] with { LastConnected = now });
        Assert.Equal([profiles[2].Id, profiles[0].Id], shell.RecentServers().Select(profile => profile.Id).Take(2));
        Assert.DoesNotContain(shell.RecentServers(), profile => profile.IsTransient);
    }

    private static ShellViewModel Shell(FakePrompts prompts, out MockBackend backend)
    {
        L.Init(AppLanguage.English);
        backend = new MockBackend();
        return new ShellViewModel(backend, new ImmediateDispatcher(), new AppSettings(), prompts);
    }
}
