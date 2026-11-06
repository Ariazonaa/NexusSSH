using NexusSSH.Core.Services;

namespace NexusSSH.Core.Tests;

public class DataFolderCheckTests
{
    private const string Me = "S-1-5-21-1-2-3-1001";
    private const int FullControl = 0x1F01FF;
    private const int Modify = 0x1301BF;
    private const int ReadAndExecute = 0x1200A9;

    [Fact]
    public void A_folder_only_the_user_and_the_machine_can_write_has_no_others()
        => Assert.Empty(DataFolderCheck.OthersWhoCanWrite(
            [
                new("S-1-5-18", @"NT-AUTORITÄT\SYSTEM", FullControl),
                new("S-1-5-32-544", @"VORDEFINIERT\Administratoren", FullControl),
                new(Me, @"PC\me", FullControl),
                new("S-1-3-0", "ERSTELLER-BESITZER", unchecked((int)0x10000000)),
                new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", @"NT SERVICE\TrustedInstaller", FullControl),
                new("S-1-5-32-545", @"VORDEFINIERT\Benutzer", ReadAndExecute),
            ],
            Me));

    [Fact]
    public void Everyone_who_may_write_besides_them_is_named_once()
        => Assert.Equal(
            [@"VORDEFINIERT\Benutzer", "Jeder", "S-1-5-21-9-9-9-1002"],
            DataFolderCheck.OthersWhoCanWrite(
                [
                    new(Me, @"PC\me", FullControl),
                    new("S-1-5-32-545", @"VORDEFINIERT\Benutzer", Modify),
                    new("S-1-5-32-545", @"VORDEFINIERT\Benutzer", 0x4),
                    new("S-1-1-0", "Jeder", 0x40000000),
                    new("S-1-5-21-9-9-9-1002", string.Empty, 0x2),
                    new("S-1-5-11", "Authentifizierte Benutzer", ReadAndExecute),
                ],
                Me));

    [Fact]
    public void A_name_from_the_directory_is_shown_without_control_characters()
        => Assert.DoesNotContain('‮', Assert.Single(DataFolderCheck.OthersWhoCanWrite([new("S-1-1-0", "Je‮der", 0x2)], Me)));
}
