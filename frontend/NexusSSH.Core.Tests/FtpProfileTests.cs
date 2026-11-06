using NexusSSH.Core.Exchange;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>FTP/FTPS als Art eines Profils: der Editor, der Weg zum Kern, der Import und die Rückfrage zum Zertifikat.</summary>
[Collection(LanguageCollection.Name)]
public sealed class FtpProfileTests
{
    public FtpProfileTests() => L.Init(AppLanguage.English);

    [Fact]
    public void Ftp_is_encrypted_unless_the_profile_says_otherwise()
    {
        var editor = new ProfileEditorViewModel { Host = "files.example", User = "web" };
        Assert.Equal((false, false, 22.0), (editor.IsFtp, editor.FtpPlain, editor.Port));

        // Mit der Wahl kommt der übliche Port – und die Verschlüsselung bleibt an.
        editor.IsFtp = true;
        var secure = editor.ToProfile();
        Assert.Equal((21, ServerProfile.Ftp, null, true, false, false), (secure.Port, secure.Protocol, secure.FtpTls, secure.IsFtp, secure.IsPlainFtp, secure.IsSsh));
        Assert.Equal("ftps://web@files.example", secure.Address);
        Assert.False(editor.ShowsFtpPlainHint);

        // Ohne Verschlüsselung nur ausdrücklich; die Warnung steht dann da, und die Adresse sagt es auch.
        editor.FtpPlain = true;
        var plain = editor.ToProfile();
        Assert.Equal((ServerProfile.FtpPlain, true), (plain.FtpTls, plain.IsPlainFtp));
        Assert.Equal("ftp://web@files.example", plain.Address);
        Assert.True(editor.ShowsFtpPlainHint);

        // Ein eigener Port bleibt und steht in der Adresse.
        editor.Port = 2121;
        Assert.Equal("ftp://web@files.example:2121", editor.ToProfile().Address);
        editor.Port = 21;

        // Zurück zu SSH: Port 22, und „ohne Verschlüsselung“ ist vergessen – auch wenn FTP später wieder gewählt wird.
        editor.IsFtp = false;
        Assert.Equal((22.0, false), (editor.Port, editor.FtpPlain));
        Assert.Equal((null, null), (editor.ToProfile().Protocol, editor.ToProfile().FtpTls));
        editor.IsFtp = true;
        Assert.Null(editor.ToProfile().FtpTls);
    }

    [Fact]
    public void Over_ftp_the_file_browser_offers_nothing_that_needs_a_command_on_the_server()
    {
        var shell = TestShell.Create(out var backend);
        var ssh = backend.Profiles.GetProfiles()[0];

        Assert.True(shell.OpenSession(ssh).Files.HasCommands);
        Assert.False(shell.OpenSession(ssh with { Id = "ftp", Protocol = ServerProfile.Ftp }).Files.HasCommands);
    }

    [Fact]
    public void The_kinds_of_connection_exclude_each_other_and_bring_their_port()
    {
        var editor = new ProfileEditorViewModel { Host = "h", User = "u", IsTelnet = true };
        Assert.Equal(23.0, editor.Port);

        editor.IsFtp = true;
        Assert.Equal((false, 21.0), (editor.IsTelnet, editor.Port));

        editor.IsTelnet = true;
        Assert.Equal((false, 23.0), (editor.IsFtp, editor.Port));

        editor.IsFtp = true;
        editor.IsSerial = true;
        Assert.Equal((false, false), (editor.IsFtp, editor.IsTelnet));
    }

    [Fact]
    public void Ftp_has_no_key_no_jump_host_and_no_identity_but_needs_a_user()
    {
        var jump = new ServerProfile("j", "jump", "j.example", 22, "u", string.Empty, []);
        var editor = new ProfileEditorViewModel(null, [jump]) { Host = "h", User = "u", ForwardX11 = true, Compression = true, LegacyAlgorithms = true };
        editor.Jump = editor.JumpChoices.First(choice => choice.Id == "j");
        editor.IsFtp = true;

        var profile = editor.ToProfile();
        Assert.Equal((null, null, false, false, false), (profile.JumpId, profile.IdentityId, profile.ForwardX11, profile.Compression, profile.LegacyAlgorithms));

        editor.User = string.Empty;
        Assert.False(editor.IsValid);
    }

    [Fact]
    public void An_existing_profile_shows_what_it_says()
    {
        var plain = new ProfileEditorViewModel(new ServerProfile("p1", "old", "h", 21, "u", string.Empty, [], Protocol: ServerProfile.Ftp, FtpTls: ServerProfile.FtpPlain));
        Assert.Equal((true, true, 21.0), (plain.IsFtp, plain.FtpPlain, plain.Port));

        var secure = new ProfileEditorViewModel(new ServerProfile("p2", "new", "h", 990, "u", string.Empty, [], Protocol: ServerProfile.Ftp));
        Assert.Equal((true, false, 990.0), (secure.IsFtp, secure.FtpPlain, secure.Port));
    }

    [Fact]
    public void The_core_gets_the_kind_and_the_wish_for_no_encryption()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.save"] = """{"ok":true,"profile":{"id":"p1","name":"old","host":"h","port":21,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true,"protocol":"ftp","ftp_tls":"none"}}""";
        using var backend = new Backend.CoreBackend(transport);

        var saved = backend.Save(new ServerProfile(string.Empty, "old", "h", 21, "u", string.Empty, [], Protocol: ServerProfile.Ftp, FtpTls: ServerProfile.FtpPlain));

        Assert.True(saved.IsPlainFtp);
        var sent = Assert.Single(transport.Sent("profiles.save")).GetProperty("profile");
        Assert.Equal(("ftp", "none"), (sent.GetProperty("protocol").GetString(), sent.GetProperty("ftp_tls").GetString()));
    }

    [Fact]
    public void The_warning_about_plain_ftp_is_a_sentence_in_the_users_language()
    {
        var transport = new FakeTransport();
        using var backend = new Backend.CoreBackend(transport);
        List<string> warnings = [];
        backend.Warning += warnings.Add;

        transport.Raise("""{"event":"core.warning","code":"ftp_unencrypted","message":"old: plain FTP"}""");

        Assert.Equal("Unencrypted FTP: the password and files cross the wire in plain text. (old: plain FTP)", Assert.Single(warnings));
    }

    [Fact]
    public void A_refused_connection_is_explained_and_the_servers_words_stay_plain_text()
    {
        var transport = new FakeTransport();
        using var backend = new Backend.CoreBackend(transport);
        List<string> warnings = [];
        backend.Warning += warnings.Add;

        transport.Raise("""{"event":"core.warning","code":"ftp_refused","message":"web: the server refused another connection – 421 5 users\u001b[31m (the maximum)"}""");

        var warning = Assert.Single(warnings);
        Assert.StartsWith("The FTP server allows no further connection", warning);
        Assert.Contains("421 5 users", warning);
        Assert.DoesNotContain('\u001b', warning);
    }

    [Fact]
    public void A_failed_active_mode_is_explained_with_what_to_do_and_the_servers_words_stay_plain_text()
    {
        var transport = new FakeTransport();
        using var backend = new Backend.CoreBackend(transport);
        List<string> warnings = [];
        backend.Warning += warnings.Add;

        transport.Raise("""{"event":"core.warning","code":"ftp_active","message":"web: no data connection from the server in active mode – 500 Illegal EPRT\u001b[31m command."}""");

        var warning = Assert.Single(warnings);
        Assert.StartsWith("In active mode no data connection came from the server", warning);
        Assert.Contains("Switch off active mode in the profile", warning);
        Assert.Contains("500 Illegal EPRT", warning);
        Assert.DoesNotContain('\u001b', warning);
    }

    [Fact]
    public void A_server_that_forgets_the_restart_position_is_explained_with_what_it_means()
    {
        var transport = new FakeTransport();
        using var backend = new Backend.CoreBackend(transport);
        List<string> warnings = [];
        backend.Warning += warnings.Add;

        transport.Raise("""{"event":"core.warning","code":"ftp_no_restart","message":"files: the server does not keep the position to continue from (REST)"}""");

        var warning = Assert.Single(warnings);
        Assert.StartsWith("This FTP server cannot continue a transfer in the middle of a file", warning);
        Assert.Contains("(REST)", warning);
    }

    [Fact]
    public void Implicit_tls_is_a_choice_of_its_own_that_brings_port_990_and_excludes_plain_ftp()
    {
        var editor = new ProfileEditorViewModel { Host = "files.example", User = "web", IsFtp = true };

        editor.FtpImplicit = true;
        var chosen = editor.ToProfile();
        Assert.Equal((990, ServerProfile.FtpImplicit, true, false), (chosen.Port, chosen.FtpTls, chosen.IsImplicitFtp, chosen.IsPlainFtp));
        Assert.Equal("ftps://web@files.example:990", chosen.Address);

        // Ohne Verschlüsselung und „von Anfang an verschlüsselt“ schließen einander aus.
        editor.FtpPlain = true;
        Assert.False(editor.FtpImplicit);
        editor.FtpImplicit = true;
        Assert.False(editor.FtpPlain);

        editor.FtpImplicit = false;
        Assert.Equal((21, null), (editor.ToProfile().Port, editor.ToProfile().FtpTls));

        // Ein gespeichertes Profil behält seinen Port, auch einen unüblichen.
        var loaded = new ProfileEditorViewModel(chosen with { Port = 2990 }, []);
        Assert.Equal((true, 2990.0), (loaded.FtpImplicit, loaded.Port));

        // Anderes als FTP kennt die Wahl nicht.
        editor.FtpImplicit = true;
        editor.IsFtp = false;
        Assert.Null(editor.ToProfile().FtpTls);
    }

    [Fact]
    public void The_active_mode_is_a_wish_of_this_computer_that_reaches_the_core_and_never_comes_from_a_file()
    {
        // Nie vorbelegt; mit FTP abgewählt, ist auch der Wunsch weg.
        var editor = new ProfileEditorViewModel { Host = "files.example", User = "web", IsFtp = true };
        Assert.False(editor.ToProfile().FtpActive);
        editor.FtpActive = true;
        var active = editor.ToProfile();
        Assert.True(active.FtpActive);
        Assert.True(new ProfileEditorViewModel(active, []).FtpActive);
        editor.IsFtp = false;
        Assert.False(editor.ToProfile().FtpActive);

        // Der Kern bekommt ihn und nennt ihn zurück.
        var transport = new FakeTransport();
        transport.Responses["profiles.save"] = """{"ok":true,"profile":{"id":"p1","name":"files","host":"files.example","port":21,"user":"web","group":"","tags":[],"favorite":false,"use_agent":true,"protocol":"ftp","ftp_active":true}}""";
        using var backend = new Backend.CoreBackend(transport);
        Assert.True(backend.Save(active).FtpActive);
        Assert.True(Assert.Single(transport.Sent("profiles.save")).GetProperty("profile").GetProperty("ftp_active").GetBoolean());

        // Aus einer Export-Datei kommt er nicht: Er lässt diesen Rechner lauschen und hängt an dessen Netz.
        var stored = active with { Id = "p1" };
        var file = DataExchange.Create([stored], new AppSettings(), "0.21.0", DateTimeOffset.UnixEpoch, "/home/a");
        Assert.DoesNotContain(typeof(ExportedProfile).GetProperties(), property => property.Name.Contains("Active", StringComparison.Ordinal));
        Assert.False(Assert.Single(DataExchange.Plan(file, [], new AppSettings(), "/home/b", keyExists: _ => true).Profiles).Profile.FtpActive);
        Assert.True(Assert.Single(DataExchange.Plan(file, [stored], new AppSettings(), "/home/a", keyExists: _ => true).Profiles).Profile.FtpActive);
    }

    [Fact]
    public void The_encoding_of_file_names_is_a_property_of_the_server_that_reaches_the_core_and_travels_in_an_export()
    {
        // Die Vorgabe ist UTF-8 – dann steht im Profil nichts.
        var editor = new ProfileEditorViewModel { Host = "files.example", User = "web", IsFtp = true };
        Assert.Equal(0, editor.FtpEncodingIndex);
        Assert.Null(editor.ToProfile().FtpEncoding);

        editor.FtpEncodingIndex = 1;
        var latin = editor.ToProfile();
        Assert.Equal(ServerProfile.FtpLatin1, latin.FtpEncoding);
        Assert.Equal(1, new ProfileEditorViewModel(latin, []).FtpEncodingIndex);
        editor.FtpEncodingIndex = 2;
        Assert.Equal(ServerProfile.FtpWindows1252, editor.ToProfile().FtpEncoding);
        Assert.Equal(2, new ProfileEditorViewModel(editor.ToProfile(), []).FtpEncodingIndex);

        // Ohne FTP gibt es nichts zu kodieren, und eine Auswahl außerhalb der Liste ist keine.
        editor.FtpEncodingIndex = 7;
        Assert.Null(editor.ToProfile().FtpEncoding);
        editor.FtpEncodingIndex = 1;
        editor.IsFtp = false;
        Assert.Null(editor.ToProfile().FtpEncoding);

        // Der Kern bekommt sie und nennt sie zurück.
        var transport = new FakeTransport();
        transport.Responses["profiles.save"] = """{"ok":true,"profile":{"id":"p1","name":"files","host":"files.example","port":21,"user":"web","group":"","tags":[],"favorite":false,"use_agent":true,"protocol":"ftp","ftp_encoding":"latin1"}}""";
        using var backend = new Backend.CoreBackend(transport);
        Assert.Equal(ServerProfile.FtpLatin1, backend.Save(latin).FtpEncoding);
        Assert.Equal("latin1", Assert.Single(transport.Sent("profiles.save")).GetProperty("profile").GetProperty("ftp_encoding").GetString());

        // Anders als „ohne TLS“ und der aktive Modus schwächt sie nichts und hängt nicht an diesem Rechner: Sie
        // kommt aus einer Export-Datei mit – aber nur ein Wert, den es gibt.
        var stored = latin with { Id = "p1" };
        var file = DataExchange.Create([stored], new AppSettings(), "0.21.0", DateTimeOffset.UnixEpoch, "/home/a");
        Assert.Equal(ServerProfile.FtpLatin1, Assert.Single(DataExchange.Plan(file, [], new AppSettings(), "/home/b", keyExists: _ => true).Profiles).Profile.FtpEncoding);
        file.Profiles = [file.Profiles![0] with { FtpEncoding = "klingon" }];
        Assert.Null(Assert.Single(DataExchange.Plan(file, [], new AppSettings(), "/home/b", keyExists: _ => true).Profiles).Profile.FtpEncoding);
    }

    [Fact]
    public void An_import_brings_ftp_profiles_but_never_the_wish_for_no_encryption()
    {
        var profile = new ServerProfile("p1", "files", "h", 21, "u", string.Empty, [], Protocol: ServerProfile.Ftp, FtpTls: ServerProfile.FtpPlain);
        var file = DataExchange.Create([profile], new AppSettings(), "0.21.0", DateTimeOffset.UnixEpoch, "/home/a");
        Assert.DoesNotContain(typeof(ExportedProfile).GetProperties(), property => property.Name.Contains("Tls", StringComparison.Ordinal));

        // Auf einem anderen Rechner: FTP ja, aber verschlüsselt – was die Verbindung schwächer macht, kommt nie aus der Datei.
        var fresh = Assert.Single(DataExchange.Plan(file, [], new AppSettings(), "/home/b", keyExists: _ => true).Profiles).Profile;
        Assert.Equal((true, false), (fresh.IsFtp, fresh.IsPlainFtp));

        // Wo es hier schon so eingestellt ist, bleibt es, und das Profil gilt als unverändert.
        var planned = Assert.Single(DataExchange.Plan(file, [profile], new AppSettings(), "/home/a", keyExists: _ => true).Profiles);
        Assert.True(planned.Profile.IsPlainFtp);
        Assert.Equal(ProfileChange.Unchanged, planned.Change);
    }

    [Fact]
    public void A_certificate_is_asked_about_in_its_own_words()
    {
        var unknown = new ConnectionPrompt.HostKey("files.example", 21, HostKeyWording.Certificate, "SHA256:new", Changed: false, null, null, _ => { });
        Assert.True(HostKeyWording.IsCertificate(unknown));
        Assert.Equal(
            ("Unknown certificate", "TLS certificate (SHA-256)", "Trust and connect"),
            (HostKeyWording.Title(unknown), HostKeyWording.Kind(unknown), HostKeyWording.Accept(unknown)));
        Assert.Contains("Windows does not know", HostKeyWording.Intro(unknown), StringComparison.Ordinal);
        Assert.Contains("exactly this certificate", HostKeyWording.Hint(unknown), StringComparison.Ordinal);

        // Ein geändertes Zertifikat: gewarnt wird wie bei einem Schlüssel, aber eine known_hosts gibt es dazu nicht.
        var changed = unknown with { Changed = true, PreviousFingerprint = "SHA256:old" };
        Assert.Equal(("The certificate has changed", "Accept the new certificate", string.Empty), (HostKeyWording.Title(changed), HostKeyWording.Accept(changed), HostKeyWording.Hint(changed)));
        Assert.Contains("different certificate", HostKeyWording.Warning(changed), StringComparison.Ordinal);

        // Ein Host-Schlüssel behält seine Worte.
        var key = new ConnectionPrompt.HostKey("h", 22, "ssh-ed25519", "SHA256:new", Changed: false, null, null, _ => { });
        Assert.Equal(("Unknown server", "ssh-ed25519"), (HostKeyWording.Title(key), HostKeyWording.Kind(key)));
        Assert.Equal("The host key has changed", HostKeyWording.Title(key with { Changed = true }));
    }
}
