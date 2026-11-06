using System.Text.Json;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Identitäten: Benutzer und Schlüssel einmal anlegen, in mehreren Profilen verwenden.</summary>
[Collection(LanguageCollection.Name)]
public sealed class IdentityTests
{
    public IdentityTests() => L.Init(AppLanguage.English);

    [Fact]
    public void A_change_to_an_identity_reaches_every_profile_that_uses_it()
    {
        var settings = new AppSettings();
        var shell = TestShell.Create(out _, settings);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var identity = shell.SaveIdentity(new Identity(string.Empty, " Deploy ", " deploy ", @" ""C:\keys\old"" ", UseAgent: false))!;

        Assert.NotEmpty(identity.Id);
        Assert.Equal(("Deploy", "deploy", @"C:\keys\old"), (identity.Name, identity.User, identity.KeyPath));
        Assert.Equal([identity], settings.Identities);
        Assert.Contains(nameof(shell.Identities), changed);

        // Zwei Profile bekommen die Identität – was sie an eigenen Angaben mitbringen, zählt nicht mehr.
        var (first, second, other) = (shell.Profiles[0], shell.Profiles[1], shell.Profiles[2]);
        shell.SaveProfile(first with { IdentityId = identity.Id, User = "someone" });
        shell.SaveProfile(second with { IdentityId = identity.Id });
        Assert.Equal(2, shell.ProfilesUsing(identity));

        ServerProfile Stored(ServerProfile profile) => shell.Profiles.Single(candidate => candidate.Id == profile.Id);
        Assert.Equal(("deploy", @"C:\keys\old", false), (Stored(first).User, Stored(first).KeyPath, Stored(first).UseAgent));

        // Der Schlüssel ändert sich an einer Stelle.
        shell.SaveIdentity(identity with { KeyPath = @"C:\keys\new", UseAgent = true });

        Assert.All(new[] { first, second }, profile => Assert.Equal(("deploy", @"C:\keys\new", true), (Stored(profile).User, Stored(profile).KeyPath, Stored(profile).UseAgent)));
        Assert.Equal((other.User, other.KeyPath), (Stored(other).User, Stored(other).KeyPath));
        Assert.Single(settings.Identities);
    }

    [Fact]
    public void Deleting_an_identity_leaves_its_profiles_with_their_own_settings()
    {
        var shell = TestShell.Create(out _);
        var identity = shell.SaveIdentity(new Identity(string.Empty, "Deploy", "deploy", @"C:\keys\id"))!;
        var profile = shell.SaveProfile(shell.Profiles[0] with { IdentityId = identity.Id })!;

        shell.DeleteIdentity(identity);

        var stored = shell.Profiles.Single(candidate => candidate.Id == profile.Id);
        Assert.Equal((null, "deploy", @"C:\keys\id"), (stored.IdentityId, stored.User, stored.KeyPath));
        Assert.Empty(shell.Identities);

        // Ein Profil, das auf eine unbekannte Identität zeigt (aus einer Datei von einem anderen Rechner), verliert nur den Verweis.
        var foreign = shell.SaveProfile(shell.Profiles[1] with { IdentityId = "gone", User = "admin" })!;
        Assert.Equal((null, "admin"), (foreign.IdentityId, foreign.User));
    }

    [Theory]
    [InlineData("", "deploy")]
    [InlineData("Deploy", "")]
    [InlineData("Deploy", "with space")]
    public void An_identity_needs_a_name_and_a_user(string name, string user)
    {
        var shell = TestShell.Create(out _);

        Assert.Null(shell.SaveIdentity(new Identity(string.Empty, name, user)));
        Assert.Empty(shell.Identities);
    }

    [Fact]
    public void The_profile_editor_takes_user_and_key_from_the_chosen_identity()
    {
        // Der Schlüssel als Pfad dieses Systems: Die Liste nennt nur den Namen der Datei, und `\` trennt nur unter Windows.
        var deployKey = Path.Combine("keys", "deploy");
        Identity[] identities = [new("i1", "Deploy", "deploy", deployKey, UseAgent: false), new("i2", "Root", "root")];
        var profile = new ServerProfile("p1", "web01", "h", 22, "ubuntu", string.Empty, [], KeyPath: @"C:\keys\own");
        var editor = new ProfileEditorViewModel(profile, [profile], identities);

        Assert.Equal(["Own settings", @"Deploy  (deploy  ·  deploy)", "Root  (root)"], editor.IdentityChoices.Select(choice => choice.Name));
        Assert.True(editor.HasOwnSignIn);
        Assert.Equal(("ubuntu", @"C:\keys\own"), (editor.User, editor.KeyPath));

        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        editor.Identity = editor.IdentityChoices[1];

        Assert.False(editor.HasOwnSignIn);
        Assert.Contains(nameof(editor.HasOwnSignIn), changed);
        Assert.Equal(("deploy", deployKey, false), (editor.User, editor.KeyPath, editor.UseAgent));
        Assert.Equal("i1", editor.ToProfile().IdentityId);

        // Zurück zu eigenen Angaben: Die Felder behalten, was dastand, und lassen sich wieder ändern.
        editor.Identity = editor.IdentityChoices[0];
        Assert.True(editor.HasOwnSignIn);
        Assert.Equal((null, "deploy"), (editor.ToProfile().IdentityId, editor.ToProfile().User));

        // Ein Profil mit Identität öffnet mit ihr; eine, die es nicht mehr gibt, zählt als keine.
        Assert.Equal("i2", new ProfileEditorViewModel(profile with { IdentityId = "i2" }, [profile], identities).Identity.Id);
        Assert.Null(new ProfileEditorViewModel(profile with { IdentityId = "gone" }, [profile], identities).Identity.Id);
    }

    [Fact]
    public void Settings_keep_only_identities_that_can_be_used_and_the_core_gets_the_link()
    {
        var settings = new AppSettings
        {
            Identities = [new("i1", "Deploy", "deploy"), new("i1", "Twice", "x"), new(string.Empty, "No id", "x"), new("i3", "Bad user", "a b"), new("i4", string.Empty, "x")],
        };

        Assert.Equal(["Deploy"], settings.Sanitize().Identities.Select(identity => identity.Name));

        var transport = new FakeTransport();
        transport.Responses["profiles.save"] = """{"ok":true,"profile":{"id":"p1","name":"web01","host":"h","port":22,"user":"deploy","group":"","tags":[],"favorite":false,"use_agent":true,"identity":"i1"}}""";
        using var backend = new Backend.CoreBackend(transport);

        var saved = backend.Save(new ServerProfile(string.Empty, "web01", "h", 22, "deploy", string.Empty, [], IdentityId: "i1"));

        Assert.Equal("i1", saved.IdentityId);
        Assert.Equal("i1", Assert.Single(transport.Sent("profiles.save")).GetProperty("profile").GetProperty("identity").GetString());
    }
}

/// <summary>Identitäten beim Export und Import – und der neue Schlüssel, der einer Identität gilt.</summary>
[Collection(LanguageCollection.Name)]
public sealed class IdentityExchangeTests
{
    public IdentityExchangeTests() => L.Init(AppLanguage.English);

    private static readonly string Home = OperatingSystem.IsWindows() ? @"C:\Users\anna" : "/home/anna";

    [Fact]
    public void Identities_travel_with_the_profiles_and_only_replace_the_very_same_one()
    {
        var key = Path.Combine(Home, ".ssh", "id_deploy");
        var settings = new AppSettings { Identities = [new("i1", "Deploy", "deploy", key), new("i2", "Root", "root")] };
        var profile = new ServerProfile("p1", "web01", "h", 22, "deploy", string.Empty, [], KeyPath: key, IdentityId: "i1");

        var file = Exchange.DataExchange.Create([profile], settings, "1.0", DateTimeOffset.UnixEpoch, Home);

        Assert.Equal(["i1", "i2"], file.Identities!.Select(identity => identity.Id));
        Assert.Equal("~/.ssh/id_deploy", file.Identities![0].KeyPath);
        Assert.Equal("i1", Assert.Single(file.Profiles!).Identity);

        // Auf einem anderen Rechner: „Deploy“ gibt es dort schon von Hand (andere Kennung, anderer Benutzer), „Root“ nicht.
        var otherHome = OperatingSystem.IsWindows() ? @"C:\Users\bob" : "/home/bob";
        var there = new AppSettings { Identities = [new("x9", "deploy", "old"), new("k1", "Only here", "me")] };
        var plan = Exchange.DataExchange.Plan(Exchange.DataExchange.Parse(Exchange.DataExchange.Serialize(file)), [], there, otherHome, _ => true);

        // Die hiesige „deploy“ bleibt, wie sie ist: Eine Datei kann Benutzer und Schlüssel einer Identität nicht
        // austauschen, nur weil sie denselben Namen nennt. „Root“ kommt dazu.
        Assert.Equal((1, 0), (plan.NewIdentities, plan.ChangedIdentities));
        Assert.Equal(["x9", "k1", "i2"], plan.Identities.Select(identity => identity.Id));
        Assert.Equal(("deploy", "old", null), (plan.Identities[0].Name, plan.Identities[0].User, plan.Identities[0].KeyPath));
        Assert.True(plan.HasChanges);

        // Das Profil verweist auf die hiesige Identität und trägt deren Angaben.
        var planned = Assert.Single(plan.Profiles).Profile;
        Assert.Equal(("x9", "old", null), (planned.IdentityId, planned.User, planned.KeyPath));

        // Dieselbe Identität (dieselbe Kennung, etwa vom eigenen zweiten Rechner) wird ersetzt.
        var own = new AppSettings { Identities = [new("i1", "Deploy", "früher")] };
        var again = Exchange.DataExchange.Plan(Exchange.DataExchange.Parse(Exchange.DataExchange.Serialize(file)), [], own, otherHome, _ => true);
        Assert.Equal((1, 1), (again.NewIdentities, again.ChangedIdentities));
        Assert.Equal(("deploy", Path.Combine(otherHome, ".ssh", "id_deploy")), (again.Identities[0].User, again.Identities[0].KeyPath));
    }

    [Fact]
    public void A_file_cannot_bring_identities_that_are_none()
    {
        var file = new Exchange.ExportFile
        {
            Format = Exchange.ExportFile.FormatName,
            Version = Exchange.ExportFile.CurrentVersion,
            Identities = [new("bad id!", "A", "u"), new("i1", " ", "u"), new("i2", "B", "a b"), new("i3", "Good", "u"), new("i3", "Twice", "u")],
            Profiles = [new("p1", "web", "h", 22, "own", Identity: "missing"), new("p2", "sw", "h2", 23, "", Protocol: "telnet", Identity: "i3")],
        };

        var plan = Exchange.DataExchange.Plan(file, [], new AppSettings(), null, _ => true);

        Assert.Equal(["Good"], plan.Identities.Select(identity => identity.Name));
        Assert.Equal([(null, "own"), (null, string.Empty)], plan.Profiles.Select(profile => (profile.Profile.IdentityId, profile.Profile.User)));
    }

    [Fact]
    public void Importing_applies_the_identities_before_the_profiles()
    {
        var settings = new AppSettings();
        var shell = TestShell.Create(out _, settings);
        var file = new Exchange.ExportFile
        {
            Format = Exchange.ExportFile.FormatName,
            Version = Exchange.ExportFile.CurrentVersion,
            Identities = [new("i1", "Deploy", "deploy")],
            Profiles = [new("n1", "new server", "new.example.org", 22, "ignored", Identity: "i1")],
        };

        var plan = Exchange.DataExchange.Plan(file, shell.Profiles, settings, null, _ => true);
        shell.ImportData(plan, new Exchange.ImportChoice());

        Assert.Equal("Deploy", Assert.Single(shell.Identities).Name);
        var profile = shell.Profiles.Single(candidate => candidate.Name == "new server");
        Assert.Equal(("i1", "deploy"), (profile.IdentityId, profile.User));

        // Ohne die Profile kommen auch die Identitäten nicht.
        var other = TestShell.Create(out _);
        other.ImportData(plan, new Exchange.ImportChoice(Profiles: false));
        Assert.Empty(other.Identities);
    }

    [Fact]
    public async Task A_new_key_can_go_to_the_identity_and_then_reaches_all_its_profiles()
    {
        var shell = TestShell.Create(out _);
        var identity = shell.SaveIdentity(new Identity(string.Empty, "Deploy", "deploy", @"C:\keys\old"))!;
        var (first, second) = (shell.SaveProfile(shell.Profiles[0] with { IdentityId = identity.Id })!, shell.SaveProfile(shell.Profiles[1] with { IdentityId = identity.Id })!);
        var session = shell.OpenSession(shell.Profiles.Single(profile => profile.Id == first.Id));
        session.Start();

        Assert.True(await shell.CreateKeyAsync(session, new KeyRequest("id_new", null, UpdateIdentity: true)));

        Assert.Equal("~/.ssh/id_new", Assert.Single(shell.Identities).KeyPath);
        Assert.All(new[] { first, second }, saved =>
        {
            var stored = shell.Profiles.Single(profile => profile.Id == saved.Id);
            Assert.Equal((identity.Id, "~/.ssh/id_new"), (stored.IdentityId, stored.KeyPath));
        });
        Assert.Contains(shell.Output, entry => entry.Text.Contains("it now belongs to the identity Deploy", StringComparison.Ordinal));

        // Ohne die Wahl löst sich das Profil von der Identität, und die bleibt, wie sie ist.
        Assert.True(await shell.CreateKeyAsync(session, new KeyRequest("id_own", null)));
        Assert.Equal((null, "~/.ssh/id_own"), (shell.Profiles.Single(profile => profile.Id == first.Id).IdentityId, shell.Profiles.Single(profile => profile.Id == first.Id).KeyPath));
        Assert.Equal("~/.ssh/id_new", Assert.Single(shell.Identities).KeyPath);
    }
}
