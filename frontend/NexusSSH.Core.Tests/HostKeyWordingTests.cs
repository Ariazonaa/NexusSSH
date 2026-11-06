using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Tests;

/// <summary>Was die Rückfrage zu einem geänderten Host-Schlüssel über den alten Eintrag sagt.</summary>
[Collection(LanguageCollection.Name)]
public sealed class HostKeyWordingTests
{
    public HostKeyWordingTests() => L.Init(AppLanguage.English);

    private static ConnectionPrompt.HostKey Changed(string? file, int? line, bool foreign)
        => new("h", 22, "ssh-ed25519", "SHA256:new", Changed: true, file, line, _ => { }, "SHA256:old", foreign);

    [Fact]
    public void An_entry_in_the_apps_own_list_will_be_replaced()
        => Assert.Equal(
            @"Previous entry: C:\x\known_hosts, line 4. That is the list NexusSSH keeps; “Accept the new key” replaces the entry.",
            HostKeyWording.ChangedHint(Changed(@"C:\x\known_hosts", 4, foreign: false)));

    [Fact]
    public void An_entry_in_the_users_file_stays_and_the_hint_names_the_line_to_remove()
    {
        var hint = HostKeyWording.ChangedHint(Changed(@"C:\Users\a\.ssh\known_hosts", 12, foreign: true));

        Assert.StartsWith(@"Previous entry: C:\Users\a\.ssh\known_hosts, line 12.", hint);
        Assert.Contains("NexusSSH never changes it", hint);
        Assert.Contains("remove line 12 there yourself", hint);
    }

    [Fact]
    public void Without_an_old_entry_there_is_nothing_to_say()
        => Assert.Equal(string.Empty, HostKeyWording.ChangedHint(Changed(null, null, foreign: false)));

    [Fact]
    public void The_german_wording_names_the_same_things()
    {
        L.Init(AppLanguage.German);
        try
        {
            var hint = HostKeyWording.ChangedHint(Changed("known_hosts", 3, foreign: true));
            Assert.Contains("Zeile 3", hint);
            Assert.Contains("NexusSSH ändert sie nie", hint);
        }
        finally
        {
            L.Init(AppLanguage.English);
        }
    }
}
